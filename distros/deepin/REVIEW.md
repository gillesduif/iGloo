# Deepin prototype review

## Initial Git state (2026-09-08)

The starting branch was `test/extract-cookies` at
`9f1c924be09e1f68b60386239bb4f548393474c2`. Its two cookie-related commits
remain on that branch. `feature/deepin` was created directly from `main` at
`2f8f8cea148915042b137536d12e9cfcbcad1890`, carrying all uncommitted files
without stashing or discarding them. `main` was not moved. The initial index
was empty. No commits or pushes were made during this review.

Before switching, the full binary diff and copies of every changed/untracked
file were saved outside the repository at
`C:\Users\GILLES~1\AppData\Local\Temp\igloo-deepin-initial-20260908-151401`.
The clean baseline below means `main`; the initial working-tree diff is an
untrusted prototype, not part of that baseline.

## Classification before implementation edits

Classification is based on the observed diff and file purpose, not authorship.
UNCERTAIN and UNRELATED files are protected and must remain byte-for-byte as
found. Generic changes require independent justification before retention.

| Initial changed/untracked file | Classification | Reason |
|---|---|---|
| `.gitattributes` | GENERIC-BUT-DEEPIN-DRIVEN | Adds LF for installer `.job` hooks. |
| `.gitignore` | UNRELATED | Ignores release documentation. |
| `Igloo.sln` | DEEPIN | Adds Deepin plugin/test projects. |
| `RELEASE-NOTES.md` | UNRELATED | Removes an existing NVMe boot-delay issue. |
| `THIRD-PARTY-NOTICES.md` | GENERIC-BUT-DEEPIN-DRIVEN | Adds the proposed ISO reader dependency. |
| `distros/_debian-family/agent/agent.py` | UNCERTAIN | Boot-order entry point removal plus formatting; no Deepin dispatch added. |
| `distros/_shared/agent/igloo_boot.py` | UNCERTAIN | Shared boot-helper import removal and formatting; provenance unclear. |
| `distros/deepin/distro.json` | DEEPIN | Changes availability, ISO pin and requirements. |
| `distros/fedora-kde/agent/agent.py` | UNRELATED | Browser prompt/log formatting. |
| `src/Igloo.App/ViewModels/DiskSelectionViewModel.cs` | GENERIC-BUT-DEEPIN-DRIVEN | Distro-specific allocation minimum. |
| `src/Igloo.App/ViewModels/MainWindowViewModel.cs` | GENERIC-BUT-DEEPIN-DRIVEN | Passes selected metadata to allocation view. |
| `src/Igloo.App/Views/DiskSelectionPage.xaml` | GENERIC-BUT-DEEPIN-DRIVEN | Binds allocation minimum. |
| `src/Igloo.Core/Abstractions/IDistroPlugin.cs` | GENERIC-BUT-DEEPIN-DRIVEN | Adds whole-ISO extraction capability. |
| `src/Igloo.Preflight/DirectInstallService.cs` | GENERIC-BUT-DEEPIN-DRIVEN | Whole-ISO extraction and recursive staged directories. |
| `src/Igloo.Preflight/Igloo.Preflight.csproj` | GENERIC-BUT-DEEPIN-DRIVEN | Adds ISO reader dependency. |
| `tests/Igloo.App.Tests/DiskSelectionViewModelTests.cs` | GENERIC-BUT-DEEPIN-DRIVEN | Allocation minimum tests. |
| `tools/chromium-crypto/chromium_import.py` | UNRELATED | Browser prompt/log formatting. |
| `distros/deepin/DeepinPlugin.cs` | DEEPIN | Prototype plugin. |
| `distros/deepin/Igloo.Distro.Deepin.csproj` | DEEPIN | Prototype packaging. |
| `distros/deepin/PartitionPolicy.cs` | DEEPIN | Prototype storage policy generator. |
| `distros/deepin/settings/hooks/before_install/10_igloo_prepare.job` | DEEPIN | Prototype installation hook. |
| `distros/deepin/settings/hooks/before_install/90_igloo_verify.job` | DEEPIN | Prototype storage check hook. |
| `distros/deepin/settings/hooks/before_install/igloo_full_disk_policy.json` | DEEPIN | Prototype partition policy. |
| `distros/deepin/settings/hooks/in_chroot/50_igloo_agent.job` | DEEPIN | Prototype agent installation hook. |
| `distros/deepin/settings/settings.ini.template` | DEEPIN | Prototype installer configuration. |
| `docs/reference/deepin-delivery-contract.md` | DEEPIN | Prototype architecture claims. |
| `docs/reference/deepin-installer-findings.md` | DEEPIN | Prototype research claims; must be reverified. |
| `src/Igloo.Preflight/BootManagerRepair.cs` | UNRELATED | Generic Windows boot-manager repair work. |
| `tests/Igloo.Distro.Deepin.Tests/DeepinPartitionPolicyTests.cs` | DEEPIN | Prototype policy tests. |
| `tests/Igloo.Distro.Deepin.Tests/DeepinPluginTests.cs` | DEEPIN | Prototype plugin tests. |
| `tests/Igloo.Distro.Deepin.Tests/Igloo.Distro.Deepin.Tests.csproj` | DEEPIN | Prototype test project. |
| `tests/Igloo.Preflight.Tests/BootManagerRepairTests.cs` | UNRELATED | Tests for protected boot-manager work. |

The existing ignored ISO and built binaries are investigation inputs/build
outputs, not newly introduced source files. The three committed cookie files
are preserved on `test/extract-cookies` and excluded from the feature baseline.

## Independent review decisions

The previous work correctly identified a custom installer, real `DI_*` settings,
OEM hooks and OSTree. It did **not** mistake Deepin for Calamares or Debian
Installer. Those correct observations do not establish that its automation is
safe. Historical VM/canary claims in its notes have not been independently
reproduced and are not counted as validation.

| Prototype choice | Decision | Evidence / reason |
|---|---|---|
| 25.2.0 direct URL and checksum | KEEP | Official download page, SHA256SUMS and local full-image hash agree. |
| Custom installer metadata, existing logo, solution/test projects | KEEP | Actual package is deepin-installer 7.0.60; normal plugin discovery convention. |
| `status: available` | DELETE | No independently verified safe dual-boot install; prototype notes themselves acknowledge unfinished enforcement. Restore coming-soon and block all executable plugin entrypoints. |
| `DI_*`/OEM configuration research | KEEP as evidence only | Keys and paths exist in the current ISO. Delivery failure and hook failure can leave stock startup running. |
| Auto custom mode (`DI_PARTITION_TYPE=6`) | DELETE | GUI still selects free space and generates partitions; not installation into an explicitly owned root. Current shell classifies it with full-disk modes. |
| First `OEMDRV`, first ESP, largest gap | DELETE | Labels can repeat; first ESP may be wrong; the largest gap need not be the space allocated by iGloo. No GUID/ownership match to the Windows manifest. |
| Live geometry policy renderer | DELETE | Accepts arbitrary string fields and operations, ignores detected GPT type, fixes sector size at 512, creates three partitions, and performs no exact extent/identity validation. Raw string interpolation is not JSON serialization. |
| Guessed consecutive partition numbers | DELETE | Lowest free number plus one/two does not describe a table with holes. Comments promise re-derivation but the running guard does not supply an exact owned-partition contract. |
| Wrapping `deepin-installer-parted` | DELETE | Only rejects some JSON patterns. Missing disk/start bypass checks; non-object rows are skipped; no end bound, exact ESP/root identity, sector-size check, operation allowlist or layout equality. `-m 4` bypasses the guard entirely. |
| Guard failure triggers delayed poweroff | DELETE | A failed preinit hook is not propagated. Copy/config/guard failures can leave the installer available. A poweroff request or missing hook is not an enforceable precondition on every writer. |
| tmpfs cover over live medium | DELETE | Deliberately hides the boot source from installer disk filtering. It neither releases the medium nor proves safe access to the shared disk. |
| Logging all `DI_*` config to ESP | DELETE | Can persist account password hashes and other configuration on a shared boot filesystem; also adds unrelated ESP writes. |
| Complete Debian-family agent reuse | REWORK, deferred | Shared agent makes package, driver, GRUB and desktop assumptions; plain reuse is unverified. Its launcher marks done even after Python failure. Use a Deepin adapter to tested operations after OOBE. |
| First-boot mount scan/bootstrap | DELETE | Scans arbitrary partitions for a seed directory, ignores copy errors, and can execute stale payload. No seed identity, completeness or retry-success guarantee. |
| Preserve immutable protection | KEEP | Shipped mount tool makes `/usr` read-only with writable `/opt`, `/etc`, `/var`; APT uses immutable adapters. No global disable is necessary. |
| Whole-ISO extraction in core | DELETE | Runs after generated GRUB configs and overwrites them with ISO originals. Adds no FAT32 per-file/path/collision safeguards; earlier boot discovery still uses Windows ISO mounting. Initrd already has ISO-file discovery; no validated consumer justifies the extension now. |
| DiscUtils dependency / notice / `.job` attributes | DELETE | Solely served the removed extraction/hooks. Normal reviewed edits return these files to the clean baseline. |
| Recursively copying every staged directory except `files` | REWORK, deferred | Nested Extras is a real generic gap, but this patch bypasses geometry substitution for nested files and lacks path/reparse controls. No consumer remains in this blocked integration. |
| Distro allocation floor in UI | REWORK, deferred | The generic idea is valid; implementation rounds down byte requirements, can overflow, and does not enforce replace capacity. Removed from this feature until an installable consumer requires a complete generic implementation. |
| Large prototype research documents | KEEP as historical input | Prominent superseded/unverified notices point to the new evidence and decision records. Do not use their claimed test sessions as proof. |

No new core capability is retained. Deepin's next implementation requires a
genuinely generic owned-target identity contract; bolting a Deepin branch onto
core would not resolve that gap.

## Protected pre-existing defect

The initial `distros/_debian-family/agent/agent.py` diff deletes
`_run_boot_order_mode`, while `main()` still calls it for `--fix-boot-order`.
That is a pre-existing failure in UNCERTAIN work, not a Deepin change. It was
reported and left untouched. The protected `BootManagerRepair` class also has
documentation referring to a nonexistent `Revert`; it was not repaired here.
