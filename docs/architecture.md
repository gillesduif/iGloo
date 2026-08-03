# iGloo Architecture

iGloo is a **two-half system**: a Windows-side *preparer* (the wizard you run) and a
Linux-side *committer* (the distro's own installer plus iGloo's first-boot agent),
communicating exclusively through files staged on dedicated partitions. This split is
forced by a fundamental constraint: destructive disk work cannot be done from the
running Windows system it targets - so **Windows stages, Linux commits**.

This document is the map. For the *why* behind individual decisions, see
[`decisions/`](decisions/); for the research-grade treatment, see the
[white paper](whitepaper/igloo-whitepaper.md).

## 1. System context

```mermaid
flowchart LR
    U(["User<br>(no Linux knowledge)"]) --> APP

    subgraph WIN["Windows machine (the target)"]
        APP["iGloo.exe<br>wizard + preparer"]
        DISK[("Target disk<br>Windows + free space")]
        APP -->|"shrink, stage, boot entry"| DISK
    end

    MIRROR["Distro mirrors<br>(HTTPS)"] -->|"ISO + checksums + GPG signatures"| APP
    DISK -->|"reboot into staged installer"| INST["Unattended Linux installer<br>(Anaconda / d-i / Ubiquity / subiquity)"]
    INST --> OS["Installed Linux<br>+ iGloo first-boot agent"]
    OS -->|"drivers, codecs, files, Wi-Fi, apps"| U
```

Trust boundaries: everything downloaded is verified (TLS + SHA-256 + GPG against a
**pinned full fingerprint**, §7) before a single byte of it is executed or staged.

## 2. Components

```mermaid
flowchart TD
    subgraph APP["src/Igloo.App - WPF wizard"]
        VM["ViewModels<br>(one per wizard step)"]
        THEME["Dark theme + Cover Flow catalog"]
    end

    subgraph CORE["src/Igloo.Core"]
        IDP["IDistroPlugin +<br>InstallerBootSpec"]
        REG["DistroRegistry / DistroLoader<br>(plugin discovery, isolated load contexts)"]
        MAN["MigrationManifest<br>(the single source of truth)"]
        THR["ThrottledProgress"]
    end

    subgraph SVC["Service projects"]
        PRE["src/Igloo.Preflight<br>hardware detection · partitioning ·<br>DirectInstallService (no-USB pipeline) ·<br>LinuxRemovalService (removal + space reclaim)"]
        ISO["src/Igloo.Iso<br>resumable download · SHA-256 ·<br>PGP verify (pinned fingerprints)"]
        MIG["src/Igloo.Migration<br>user-file staging"]
        USB["src/Igloo.UsbWriter<br>fallback USB path"]
    end

    subgraph PLUG["distros/ - one folder per distro"]
        FED["fedora-kde<br>Anaconda / kickstart"]
        DEB["debian<br>d-i / preseed"]
        MINT["linuxmint-cinnamon<br>Ubiquity / preseed"]
        UBU["ubuntu (in development)<br>subiquity / autoinstall"]
        FAM["_debian-family<br>shared first-boot agent (Python)"]
    end

    APP --> CORE
    APP --> SVC
    SVC --> CORE
    REG --> PLUG
    DEB -.uses.-> FAM
    MINT -.uses.-> FAM
    UBU -.uses.-> FAM
```

Rules of the dependency graph:

- **Core never references a concrete distro.** Plugins are discovered at runtime from
  `distros/` and loaded into isolated `AssemblyLoadContext`s.
- **Plugins never touch the disk.** They *declare* (boot spec) and *render* (config,
  agent payload); all disk work happens in `DirectInstallService`, identically for
  every distro.
- The **manifest** (`migration-manifest.json`) is written once by the wizard and read
  by everything downstream - installer configs are rendered from it on Windows, and
  the first-boot agent applies it on Linux.

## 3. The wizard (happy path with its safety gates)

```mermaid
flowchart TD
    A["1 · Welcome"] --> B["2 · Preflight<br>UEFI · BitLocker · disks · GPU · RAM"]
    B -->|"blocker found"| BX(["STOP - reason + remedy shown"])
    B --> C["3 · Distro catalog"]
    C -->|"plugin CheckCompatibility<br>blocker (e.g. RAM floor)"| CX(["distro greyed out<br>with the reason"])
    C --> D["4 · ISO download<br>SHA-256 + GPG (pinned)"]
    D -->|"verification fails"| DX(["STOP - never installs<br>an unverified image"])
    D --> E["5 · Migration setup<br>folders · browser · apps · account"]
    E --> F["6 · Disk selection<br>dual-boot size / replace"]
    F --> G["7 · File staging +<br>installer config rendering"]
    G --> H["8 · Direct install<br>partition · stage · BootNext"]
    H --> I(["Reboot into unattended installer"])
```

## 4. The no-USB direct-install pipeline

What `DirectInstallService` does between "Install" and the reboot:

```mermaid
sequenceDiagram
    autonumber
    participant W as Wizard
    participant D as DirectInstallService
    participant P as Plugin
    participant DSK as Target disk
    participant FW as UEFI firmware

    W->>P: GetInstallerBootSpec()
    P-->>W: declarative boot spec (cmdline, paths, delivery)
    W->>D: Prepare(disk, size, iso, staging, bootSpec)
    D->>DSK: shrink Windows (Resize-Partition via WMI)
    D->>DSK: create FAT32 seed partition (OEMDRV / CIDATA)
    alt ISO ≥ 4 GiB (FAT32 file limit)
        D->>DSK: create NTFS ISO partition (IGLOOISO)
    end
    alt subiquity distro
        D->>DSK: pre-create Linux root partition (installer must never add one)
    end
    D->>DSK: extract or download kernel + initrd
    D->>DSK: inject rendered config into initrd (gzip-appended cpio) or copy to seed
    D->>DSK: copy full ISO (iso-scan / casper distros)
    D->>DSK: write grub.cfg to every prefix the distro's GRUB searches
    D->>DSK: copy manifest + agent payload
    D->>FW: register one-shot BootNext entry
    Note over FW: If anything fails to boot,<br>the NEXT reboot lands back in Windows.
    W->>FW: reboot
```

Resulting disk layout (dual-boot, large-ISO case):

| # | Partition | FS | Owner | Fate after install |
|---|---|---|---|---|
| 1 | EFI System Partition | FAT32 | shared | reused by GRUB - never reformatted |
| 2 | MSR | - | Windows | untouched |
| 3 | Windows C: | NTFS | Windows | untouched (read-only source for file migration) |
| 4 | Recovery | NTFS | Windows | untouched |
| 5 | Seed - `OEMDRV`/`CIDATA` | FAT32 | iGloo | read at install + first boot; deletable afterwards |
| 6 | `IGLOOISO` (only if ISO ≥ 4 GiB) | NTFS | iGloo | consumed at install; deletable afterwards |
| 7 | Linux root | ext4 | Linux | the new OS |

## 5. Two-phase first-boot bootstrap (the pattern Mint proved)

Installer late-hooks run in a **hostile environment**: busybox tooling, no udev
labels, and a live kernel whose modules do not match `/target` - on Mint that made
mounting the FAT32 seed *impossible at install time*. The rule that came out of it:

> **Never do environment-sensitive work in an installer hook. Write a bootstrap;
> let the installed system do the work on its own first boot.**

```mermaid
sequenceDiagram
    autonumber
    participant IH as Installer late-hook<br>(hostile - wrong modules, busybox)
    participant T as /target (installed system)
    participant FB as First boot (real OS)
    participant A as iGloo agent

    IH->>T: write igloo-bootstrap.sh (echo only - nothing that can fail)
    IH->>T: write + enable oneshot unit (Before=display-manager)
    Note over IH: no mounts, no label lookups,<br>no module-dependent syscalls
    FB->>FB: mount seed partition (vfat works - own kernel!)
    FB->>T: copy agent.py + manifest from seed
    FB->>A: exec agent (before any login screen)
    A->>A: password · keyboard · drivers (NVIDIA) · codecs ·<br>os-prober/GRUB · Flathub · file migration (ntfs-3g) ·<br>Wi-Fi keyfiles · redact secrets · mark .done
```

## 6. One pipeline, four installer stacks

The entire per-distro boot delta fits in one declarative record
([`InstallerBootSpec`](../src/Igloo.Core/Abstractions/IDistroPlugin.cs)); the
pipeline never branches on distro identity, only on spec fields:

| | Fedora KDE | Debian 13 | Mint Cinnamon | Ubuntu *(in dev)* |
|---|---|---|---|---|
| Installer | Anaconda | debian-installer | Ubiquity (casper) | subiquity (casper) |
| Config format | kickstart | preseed | preseed | cloud-init autoinstall |
| Config delivery | seed volume (`OEMDRV`) | injected into initrd | injected into initrd | seed volume (`CIDATA`) |
| Boot payload | kernel+initrd+stage2 from ISO | **hd-media** kernel/initrd (downloaded) + full ISO | kernel/initrd + full ISO | kernel/initrd + full ISO (NTFS) + `toram` |
| Free-space strategy | guarded `clearpart` + `%pre` | `biggest_free` (method **unset**!) | `biggest_free` (method **unset**!) | all-preserved table; root **pre-created by iGloo** |
| Agent hand-off | `%post` | busybox partition scan | two-phase bootstrap | two-phase bootstrap |

Hard-won, non-obvious rules encoded in the code and templates (violating any of
these caused a real failure - see the white paper's field-notes section):

1. Installer "easy" partitioning presets (`partman-auto/method`, subiquity
   `layout:`) mean **whole-disk wipe**, never dual-boot.
2. curtin storage v2 configs are **authoritative**: any partition not declared is
   *deleted*; preserved entries need real `number/offset/size/partition_type/uuid`
   or the GPT rewrite clobbers Windows' identity.
3. FAT32 cannot hold a file ≥ 4 GiB → oversized ISOs get their own NTFS partition.
4. Each signed GRUB searches a compiled-in prefix (`/EFI/fedora`, `/EFI/debian`,
   `/boot/grub`…) → write `grub.cfg` to *all* candidate prefixes.
5. Subiquity early-command payloads must be single-quoted (double quotes are
   pre-expanded by an outer shell) and must never `pkill -f` (self-match).

## 7. Security model

```mermaid
flowchart LR
    URL["HTTPS-only URLs<br>(HTTP rejected outright)"] --> DL["Resumable download"]
    DL --> SHA["SHA-256<br>(pinned or from signed checksum file)"]
    KEY["Signing key<br>bundled with app, else fetched"] --> FPR{"full 160-bit<br>fingerprint match?"}
    FPR -->|no| STOP1(["ABORT"])
    FPR -->|yes| GPG["GPG verify checksum file<br>(cleartext or detached)"]
    SHA --> OK{"all checks pass?"}
    GPG --> OK
    OK -->|no| STOP2(["ABORT - never degrade<br>to a warning"])
    OK -->|yes| USE["Stage for install"]
```

- **Fail-closed:** a declared signature that cannot be verified is an error, never a
  warning. No SHA-256 available → no download.
- **Fingerprints, not key IDs:** 64-bit key IDs are forgeable on keyservers; iGloo
  pins the full fingerprint and prefers keys bundled with the app.
- **Secrets hygiene:** the manifest's password is used once (chpasswd) and then
  redacted on disk; Wi-Fi keyfiles land `0600 root:root`.

## 8. Failure containment (the "worst case is Windows still boots" invariant)

| Threat | Containment |
|---|---|
| Installer never boots | `BootNext` is one-shot: next reboot lands in Windows |
| Installer crashes mid-run | Config only ever targets the freed space / pre-made partitions; Windows partitions are declared preserved everywhere |
| Windows-side crash mid-wizard | Each step is resumable; partitions are labelled and re-detected (`FindExistingOemDrv`) |
| Silent config corruption | Fail-loud guard: unsubstituted `{{IGLOO_*}}` tokens abort on Windows, *before* reboot |
| Unattended step fails invisibly | Every phase writes a persistent trace (`/var/log/igloo*`, watchdog logs name the holders) |
| Bad shrink | Windows' own `Resize-Partition`; no custom NTFS logic, no unmovable-file tricks |

## 9. Adding a distro (contributor path)

1. Copy [`distros/_template/`](../distros/_template/); fill `distro.json`
   (ISO URL, SHA-256/signature URLs, **GPG key + full fingerprint**, tags).
2. Implement `IDistroPlugin`: compatibility findings, config rendering from the
   manifest, agent payload (reuse `_debian-family` for apt distros), and the
   `InstallerBootSpec`.
3. Study the matrix in §6 first - the pitfalls per installer stack are already
   solved once; do not rediscover them.
4. Validate in a VM: unattended install, **Windows survives**, agent log clean.

Full guide: [`distros/README.md`](../distros/README.md).

## See also

- [`decisions/`](decisions/) - ADRs (plugin model, licensing, runtime injection, …)
- [`../distros/ubuntu/STATUS.md`](../distros/ubuntu/STATUS.md) - the Ubuntu engineering dossier
- [`whitepaper/igloo-whitepaper.md`](whitepaper/igloo-whitepaper.md) - research-grade write-up
- [`guide/`](guide/) - visual walkthrough (shot list, beta)
