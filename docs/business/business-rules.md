# Business rules

The hard constraints of iGloo. Each rule has a rationale and a **traceability
column**: the code or template that enforces it. A change that violates one of
these is a bug by definition, whatever the tests say. Rules were paid for in
field failures - see the white paper's field notes for the receipts.

| # | Rule | Rationale | Enforced by |
|---|---|---|---|
| **BR-01** | **Windows must survive any failure.** Worst case of every code path is "Windows still boots, unmodified beyond reported steps". | The product repartitions strangers' disks; one wiped Windows ends the project's trust permanently. | One-shot `BootNext` ([DirectInstallService.RegisterBootEntryAsync](../../src/Igloo.Preflight/DirectInstallService.cs)); free-space-only installer configs; all-preserved storage declarations |
| **BR-02** | **Never use an unverified image.** SHA-256 **and** (when the distro publishes one) a GPG signature verified against a key pinned by full 160-bit fingerprint. Verification failure is fatal, never a warning. | Supply-chain attack on an OS image = total compromise of the user's machine. | [IsoAcquisitionService](../../src/Igloo.Iso/IsoAcquisitionService.cs) (fail-closed policy block), [Pgp*Verifier](../../src/Igloo.Iso/PgpCleartextVerifier.cs) fingerprint pin |
| **BR-03** | **Installers never partition the shared disk.** They install only into space iGloo freed; on curtin they may not create/renumber any partition (iGloo pre-creates root). Installer partitioning presets (`partman-auto/method`, subiquity `layout:`) are forbidden - they mean whole-disk wipe. | Three distros' "easy" presets each turned out to erase the entire disk. | Preseed/kickstart templates (guard comments), [BuildStoragePartitionList / EnsureRootPartition](../../src/Igloo.Preflight/DirectInstallService.cs) |
| **BR-04** | **Zero interaction from "Install" to usable desktop.** A prompt in the unattended phase is a defect, even a cosmetic one. | The target user cannot answer installer questions; a paused install looks like a broken PC. | Rendered configs (`layoutcode`, `use_nonfree`, autoinstall sections); agent covers what installers ignore |
| **BR-05** | **Migration is opt-in and secrets are ephemeral.** Only user-selected folders/browser data move; the manifest password is redacted after use; Wi-Fi keyfiles land `0600 root:root`. | Data custody and least surprise. | Wizard scope selection; agent `redact-manifest` + keyfile permissions ([agent.py](../../distros/_debian-family/agent/agent.py)) |
| **BR-06** | **Block known-fatal configurations before any disk change.** BitLocker-locked volumes, RAM below a distro's floor (e.g. Ubuntu's 10 GB toram budget), Secure Boot for distros that cannot take it - all stop the wizard *with a remedy*, at preflight. | Failing after repartitioning has begun ruins the experience BR-01 protects. | Plugin `CheckCompatibility` wired into the catalog ([DistroSelectionViewModel](../../src/Igloo.App/ViewModels/DistroSelectionViewModel.cs)) - Blockers grey the distro out |
| **BR-07** | **Every unattended phase leaves a persistent, on-disk trace.** No silent success and no silent failure; a failed run must be diagnosable from its logs alone, without reproduction. | Debugging blind cost this project entire weeks; users cannot reproduce on demand. | `set -x` traces (`/var/log/igloo*`), watchdog holder logs, fail-loud `{{IGLOO_*}}` token guard |
| **BR-08** | **Plugins declare; the core executes.** A distro plugin renders configs and declares a boot spec but never touches disks, firmware, or the network. | Community plugins must be reviewable in minutes and unable to harm the machine. | `IDistroPlugin` surface ([IDistroPlugin.cs](../../src/Igloo.Core/Abstractions/IDistroPlugin.cs)); disk work only in `DirectInstallService` |
| **BR-09** | **Leaving must be as easy as trying.** iGloo will remove a Linux install as cleanly as it created one (M13) and undo a fresh migration (M14). | Reversibility is the psychological unlock for trying Linux at all. | Roadmap M13/M14 *(planned - rule adopted, enforcement pending)* |
| **BR-10** | **HTTPS everywhere.** Any non-HTTPS artefact URL (ISO, checksum, signature, key) is rejected outright. | Plain HTTP hands an on-path attacker checksum, signature and key in one go. | [RequireHttps](../../src/Igloo.Iso/IsoAcquisitionService.cs) |

## Applying the rules

- **Reviews:** a PR touching partitioning, verification, or the unattended path
  names the BRs it affects and how it keeps them intact.
- **New distros:** BR-02, BR-03, BR-04 and BR-07 are the acceptance checklist for
  a plugin's first validated run.
- **Conflicts:** BR-01 outranks everything, including shipping dates - Ubuntu was
  parked (see [STATUS.md](../../distros/ubuntu/STATUS.md)) rather than shipped
  with a partitioning path that could not yet prove BR-01/BR-03 end to end.
