# Debian Trixie target-root deployment — issue #241

Latest UserData status: the bounded [canonical selected-document result](debian-userdata-path-policy-canonical-evidence.json)
passed independent transfer, protected admission, whole-root readback and exact
teardown on 2026-10-01; its protected chains reopened again on 2026-10-03.
Agent installation, DeploymentContent, Enrollment and first-boot completion remain
pending. Historical results below retain their original meanings.

Status: **DEBIAN CONTENT DEPLOYMENT FOUNDATION BLOCKED** (2026-09-28 continuation).
The [configured-root artifact continuation](debian-configured-root-artifact.md)
now includes a reviewed, expiring wsdd recommendation exception, a complete signed
1,594-package closure and a real configured factory root with clean package readback.
The [neutralization/import continuation](debian-configured-root-neutralization.md)
preserves the raw source, exactly neutralizes a separate root and independently
verifies a real development artifact. Production authentication/import remain
Unsupported. Older residue and unresolved-closure results below are historical.
Its responsibility matrix supersedes the old on-target bootstrap intent,
without changing existing v1 receipt history or enabling package dispatch.
The latest [bootstrap primitive decision](debian-bootstrap-primitive.md) rejects
stock debootstrap for the existing restricted execution boundary. No replacement
has a qualified complete lifecycle; earlier candidate recipes below are historical.
The candidate native bootstrap command has been removed. Native
preparation and firmware registration remain disabled. This Debian-only checkpoint
does not change Fedora, Mint, RecoverySnapshotV1, RecoveryReadiness or PreCommitGate.

## Implemented boundary and limits

`distros/debian/Deployment` now implements a 43-stage candidate protocol, exact
ownership/mount guards, source/package/configuration contracts, independent
readback requirements, durable intent/reopen sequencing, failure classification,
and a Debian producer extending the shared installation receipt. It uses the
existing `PreparedLayoutV1`, `InstallationOwnership`, `InstallerEspBinding`, Linux
inventory, `TargetRootMounts`, payload verifier, firmware observation and shared
receipt types. There is no second Windows observation model.

`DebianNativeDeploymentOperations` now dispatches a bounded subset of typed stage
instructions to `IDebianIsolatedStageHost`. That host and the independent deployment
observer still have **no qualified Linux implementation**. `DebianLinuxDeploymentJournal`
now uses a pinned Python tool with create-new files, file/directory fsync and fresh
process reopen. It is tested on temporary Linux fixtures, not a migration filesystem.
The observer's typed checks must come from independent native
readback with retained audit records; neither a digest nor `exit 0` proves a check.
The journal requires exclusive generation reservation, create-new durable writes
and independent reopen. Power-loss durability on the intended deployment filesystem
still needs VM validation. The existing Windows artifact mechanism remains unchanged.

`DebianPlugin.RenderInstallerConfigAsync`, `GetInstallerBootSpec` and
`GetAgentPayloadAsync` now reject the legacy path unconditionally. Merely changing a
future capability flag cannot re-enable it. The historical preseed is retained for audit; no
fallback to biggest_free, atomic, default bootdev or installer-selected ESP is
available through configuration export. The global preparation guard remains
Unsupported before the first storage side effect.

## Inputs and ownership

`DebianDeploymentPlanV1` binds the complete shared ownership record, independently
verified formatted-root receipt, generation, authenticated-source declaration,
target configuration and generation-bound agent payload. Root formatting remains
outside this engine. An existing filesystem without that receipt is not ownership.
The plan fingerprint is SHA-256 over its exact serialized inputs, not a new
RecoverySnapshot semantic hash. No persisted device ordinal authorizes an action.

Every stage freshly resolves GPT disk/partition/type/geometry and filesystem UUIDs.
Duplicate, missing, changed or aliased identities stop before the operation.
Root cannot alias a preserved partition; Linux ESP cannot alias Windows ESP.
The existing Linux hardware-ID equivalence and native collector qualification gaps
are not solved by these pure comparisons. A second guard acquisition follows
durable intent reopen immediately before calling an operation.

## Exact stage contract

All paths below are relative to the verified owned target unless identified as
runtime paths. Every operation carries the same generation and plan fingerprint.
The `Checks` column names required independent evidence, not just stdout.

| # | Stage | Inputs/owned target, intended effects and required checks |
| --- | --- | --- |
| 1 | ValidateOwnership | Durable layout/root receipt and fresh inventory; validate complete preserved/owned set. |
| 2 | ResolveRuntimeDevices | Re-resolve same identities; device names are transient outputs only. |
| 3 | PrepareNamespace | Private mount/PID namespace and effective device, capability, firmware, network and service isolation. Native implementation unqualified. |
| 4 | MountRoot | Exact EXT4 root at `/run/igloo/target/<generation>`; independent mount/path/device readback. No format. |
| 5 | MountLinuxEsp | Exact existing FAT32 Linux ESP at target `/boot/efi`; no format, no alternate ESP. |
| 6 | MountPayload | Exact OEMDRV at `/run/igloo/source/<generation>`, read-only. |
| 7 | ExcludeWindowsEsp | Complete mount inventory; Windows ESP unmounted, other preserved filesystems not writable, no aliases. |
| 8 | VerifySourceTrust | Independently authenticated InRelease/index/archive chain, keyring/debootstrap hashes and complete dependency solution. |
| 9 | ObserveFirmwareBeforePackages | Complete relevant native baseline before bootstrap/package scripts; missing observations block. |
| 10 | Bootstrap | Debootstrap trixie/amd64/minbase, forced GPG verification, explicit local repository and keyring, exact target root; inspect dpkg/source state. |
| 11 | ConfigureApt | Separate offline deployment source and final Debian/updates/security deb822 sources. No unauthenticated fallback. |
| 12 | ConfigureArchiveKeyring | Exact authenticated Debian keyring at target path; verify content and trust policy. |
| 13 | MountHelpers | Explicit private `/dev`, `/dev/pts`, `/proc`, read-only `/sys`, private `/run`, read-only payload alias; no recursive host binds. |
| 14 | ConfigurePackagePolicy | Supported no-service-start policy, explicit offline APT policy, audited GRUB debconf settings; read back effective values. |
| 15 | ConfigureHostname | Validated hostname and deterministic hosts file. |
| 16 | ConfigureLocale | UTF-8 locale.gen/default locale plus actual locale generation/readback. |
| 17 | ConfigureTimezoneKeyboard | Zoneinfo-bound localtime and explicit keymap; validate release data, no hardware clock write. |
| 18 | InitializeMachineIdentity | No copied runtime machine ID, D-Bus ID, NetworkManager secret or SSH host keys. |
| 19 | ConfigureNetwork | NetworkManager, loopback-only interfaces, explicit resolver link, verified migrated profiles. No installer credentials copied implicitly. |
| 20 | GenerateFstab | Shared generator: exact EXT4 root UUID and Linux ESP FAT UUID only. No raw device path, Windows or OEMDRV entry. |
| 21 | ConfigureUser | Preplanned username/UID 1000 absent first; protected credential reference, account/shadow/group verification. No password in arguments/receipt. |
| 22 | ConfigureSudo | Locked root, intended user in sudo group, default authenticated sudo policy; validate effective sudoers. |
| 23 | InstallKernel | Pinned linux-image-amd64 and resolved signed image/modules; dpkg, artifact and firmware-unchanged checks. |
| 24 | InstallFirmware | Resolved hardware package policy; configured packages and firmware-unchanged evidence. Unsupported DKMS/MOK case blocks. |
| 25 | InstallDesktop | GNOME task/product closure including display manager; dpkg/desktop and firmware-unchanged evidence. |
| 26 | InstallAgent | Generation/content-bound agent, manifest and new deployment service installed in root; configuration/service readback. Legacy first-boot agent is not a qualified profile. |
| 27 | ConfigureSignedPackages | Audited Debian signed shim/GRUB packages, exact mounted ESP, isolation; inspect dpkg/policy and firmware unchanged. |
| 28 | DrainPackageTriggers | Require configured packages/no pending signing triggers and unchanged firmware; dpkg success alone insufficient. |
| 29 | FinalizeLoaderFiles | Candidate documented signed-mode grub-install, exact `/boot/efi`, id debian, `--no-nvram`; inspect signed files and unchanged firmware. |
| 30 | GenerateGrubConfiguration | Controlled target defaults/scripts, os-prober disabled, inspect root UUID/kernel/initramfs references. |
| 31 | FinalizeMachineIdentity | Recheck identity after package hooks; remove temporary service/APT/resolver policy, verify intended installed state. |
| 32 | GenerateInitramfs | Generate for installed target kernel after configuration; independently verify matching modules and image. Never use runtime uname as authority. |
| 33 | InspectBootFiles | Reopen exact EFI/kernel/initramfs/GRUB files, check paths, identity and required signed-chain profile. |
| 34 | ObserveFirmwareBeforeFinalization | Independent baseline and unchanged package-stage firmware; no implicit package-created entry accepted. |
| 35 | FinalizeFirmware | **Unimplemented separate journal boundary** for explicit permanent NVRAM intent; no writer or automatic index selector supplied. Package namespace continues to deny writes. |
| 36 | InspectFirmwareAfterFinalization | Full native before/after delta, expected raw bytes/attributes/targets; reject any undeclared change or missing observation. |
| 37 | VerifyWindowsPreservation | Reinspect GUID/type/geometry/filesystem identity and before/after Windows ESP content evidence; no formatting or package target there. |
| 38 | PersistDeploymentEvidence | Produce shared+Debian detail, durable independent reopen; not reboot permission. |
| 39 | UnmountHelpers | Reverse recorded dependency order, no force/lazy unmount; independently prove absence. |
| 40 | UnmountLinuxEsp | Exact recorded mount/device only; flush and inspect disappearance. |
| 41 | UnmountPayload | Exact payload mount only; inspect disappearance. |
| 42 | UnmountRoot | Exact root mount only; inspect disappearance. Failure still prevents completion. |
| 43 | PersistCompletionEvidence | Final evidence includes successful teardown; independent reopen. No reboot call exists. |

`NotStarted` means the operation was not called (for example failed guard).
Durable intent with no verified result is OutcomeUnknown. A nonzero tool result
with observable partial state is Failed; missing/denied/unsupported/ambiguous
readback or an exception after entry is OutcomeUnknown. Check availability and
comparison verdict are distinct; missing verdict never defaults to success.
Prior successful stages and partial readback remain recorded. Any failed/unknown
stage stops progression. The runner is single-use and the journal must refuse an
already-reserved generation. No automatic retry, cleanup-on-error, rollback or
transactionality is claimed. Interrupted mounts need an explicit recovery action.

## Mount and configuration model

The core shared verifier remains authoritative. The Debian extension checks each
declared helper's type, path, parent, device/mode, propagation and symlink readback
before removing only those inspected helpers from the core-view comparison. Any
other submount (including another ESP or efivarfs) is rejected. Partial mount
sequences and reverse teardown are checked independently too.

The proposed helpers are tmpfs `/dev`, new devpts `/dev/pts`, proc in the private
PID namespace, read-only sysfs, tmpfs `/run`, and a read-only bind of the **owned**
payload at `/run/igloo-source`. Only a qualified backend can establish device-node
allowlisting, no host service access, mount escalation denial and firmware access
denial. A requested flag or chroot is not that evidence. Debootstrap's own helper
mount/device requirements must be qualified in this environment; denying access
without testing successful package semantics is not sufficient.

Deterministic configuration outputs cover hostname/hosts, locale, timezone/keymap,
NetworkManager resolver ownership, Debian apt sources, GRUB defaults and temporary
state removal. The current candidate intentionally leaves `/etc/machine-id` absent
at completion and D-Bus linked to it, requesting initialization on the first
installed boot. This differs from an empty file and requires first-boot service
validation; no installer ID is copied. SSH server is not in the baseline policy;
unexpected server/host-key installation must be handled by the qualified package
profile. See [systemd's machine-id contract](https://manpages.debian.org/trixie/systemd/machine-id.5.en.html).
No swap partition is added implicitly; swap/hibernation product policy remains a
qualification item. Fstab currently has only the two owned system filesystems.

## Source authenticity and workstation policy

The supported primitive is Debian's [debootstrap](https://manpages.debian.org/trixie/debootstrap/debootstrap.8.en.html),
not a complete desktop installer. The existing product requires offline install;
this continuation does not silently require working Wi-Fi during installation.
The command contract uses an owned local repository, explicit keyring,
`--force-check-gpg`, `--arch=amd64`, `--variant=minbase`, and `trixie` (never stable).
The final installed system gets official HTTPS trixie/updates/security sources;
deployment uses the separate local source in a network-denied environment.

`DebianSourceV1` binds bundle manifest/content identity, keyring/debootstrap hashes,
archive origins, signing fingerprints, InRelease/Packages hashes, validity times,
resolved package names/versions/architectures/lengths/hashes and dependency-solution
identity. Source authentication failure/expiry/change blocks. A release codename
does not pin package bytes. The independently authenticated source observer and
offline bundle/dependency solver are **not implemented**. Current research hashes
are provenance only, not a verified Debian archive trust root. See
[apt-secure](https://manpages.debian.org/trixie/apt/apt-secure.8.en.html).

The declared baseline includes:

- linux-image-amd64, initramfs-tools and the resolved signed kernel/modules;
- task-gnome-desktop, gdm3, NetworkManager, sudo, systemd-sysv, dbus;
- locales, tzdata, console/keyboard configuration, adduser and certificates;
- firmware-linux, firmware-iwlwifi, firmware-realtek plus a reviewed hardware closure;
- Python/GTK bindings, rsync and ntfs-3g for intended migration work;
- Debian archive keyring, signed shim/common, signed GRUB/GRUB tools, efibootmgr and mokutil.

`standard` is a tasksel task, **not a package called task-standard**. Its selected
standard-priority package closure and GNOME recommendations must be expanded and
pinned. `task-gnome-desktop` depends on task-desktop/gnome-core and recommends gnome;
installing just the minimal dependency set is not the intended product. This
baseline is tested intent, not a complete authenticated offline package solution.
NVIDIA/DKMS/MOK, codecs, suggested applications, migrated Wi-Fi/profile permissions,
localization data, firmware coverage and first-boot migration remain unqualified.

Legacy behavior classification: GNOME, intended account/sudo, Wi-Fi and user-data
migration are product requirements. Apt/locales/timezone/kernel are Debian setup.
biggest_free/atomic/default bootdev, d-i late_command and broad OEMDRV scans are
rejected installer mechanisms. First-boot password repair, ignored failures and
description-selected boot cleanup cannot become success criteria in the new engine.

## Actual Trixie package observations

Read-only control/data extraction, never dpkg installation or maintainer-script
execution, covered cached GRUB 2.12-9+deb13u2, signed GRUB 1+2.12+9+deb13u2,
shim 1.51~1+deb13u1+16.1-2~deb13u1 and debootstrap 1.0.141. This continuation also
downloaded/extracted efibootmgr 18-2, mokutil 0.7.2-1 and tasksel 3.81. Debian's
current main index identified initramfs-tools 0.148.4, NetworkManager 1.52.1-1,
linux-image-amd64 6.12.107-1 and task-gnome-desktop 3.81. These are observed versions,
not immutable production pins. Artifact hashes are in
[debian-deployment-evidence.json](debian-deployment-evidence.json).

- GRUB EFI postinst tests existing core.efi, GRUB_DISTRIBUTOR and debconf flags;
  it can call grub-install. Its wrapper can report failure without failing dpkg.
- Shim postinst may reinstall into the existing `/boot/efi/EFI/<id>` and likewise
  swallow installation failure. It then invokes update-secureboot-policy.
- shim-signed-common configure/trigger calls that policy directly. The policy
  can invoke `mokutil --enable-validation` / `--disable-validation`; no-nvram
  GRUB configuration does not constrain it. No script patch or disabling Secure
  Boot is proposed.
- Signed GRUB's inspected Debian control archive has no equivalent multi-ESP
  postinst enumerator; this does not establish arbitrary hooks harmless.
- Inspected efibootmgr/mokutil control archives have no maintainer scripts. Their
  binaries are still firmware mutation tools when invoked by other packages.
- Kernel hooks/update-grub and os-prober require target-state verification.
  The candidate disables os-prober during this ownership boundary.

Public `grub2/update_nvram=false`, `grub2/force_efi_extra_removable=false` and
[policy-rc.d](https://manpages.debian.org/trixie/init-system-helpers/invoke-rc.d.8.en.html)
are modeled as configuration, not a sandbox. The candidate final file command is
`grub-install --target=x86_64-efi --efi-directory=/boot/efi --boot-directory=/boot --bootloader-id=debian --uefi-secure-boot --no-nvram`.
[Those options are documented](https://manpages.debian.org/trixie/grub2-common/grub-install.8.en.html).
No force, unsigned fallback, removable fallback or default disk is generated.
Actual signed helper files, shim/GRUB/kernel compatibility, SBAT/revocation policy,
package behavior under isolation and driver/MOK interactions still require a
qualified loader profile and VM evidence. Hashes alone do not prove Secure Boot.

Permanent NVRAM is deliberately a separate unresolved journal boundary.
[efibootmgr](https://manpages.debian.org/trixie/efibootmgr/efibootmgr.8.en.html)
documents create-with-order-update, create-only and explicit order operations;
no command is generated here. The pure delta comparator reuses canonical raw
firmware observations and rejects undeclared changes, attribute failures and
missing observations. New/deleted slots require explicit Absent evidence.
Complete native enumeration, immutable final intent, bounded writer, interruption
recovery and independent loader/entry readback are still absent. No code claims
efibootmgr failure means firmware was untouched.

## Agent and receipts

The agent contract binds OEMDRV volume/partition/generation and exact content for
agent.py, migration-manifest.json and a **new unimplemented** deployment service.
The current shared legacy agent is not accepted: it discovers seed partitions,
performs broad OS/boot discovery, changes BootOrder/cleans firmware entries, and
returns success after recorded step failures. Its launcher writes `.done` even
after agent failure. This continuation does not modify that shared agent or run it.
An ownership-bound profile with explicit failure evidence must replace that behavior.

`DebianInstallationReceiptV1` contains the immutable plan/source/package policy,
all detailed stage outcomes, installed package/version/status inventory and the
shared `InstallationReceiptV1` (root/ESP/generation, fstab/kernel/initramfs/EFI/agent
file identities, firmware witness and shared stage mapping). Its codec validates
structure, plan/hash/generation and independent reopen. Shared success flags must
map to completed Debian stages. Assessment compares fresh package/file/firmware
evidence using existing rules and requires every detailed stage, including teardown.
Partial, corrupt, mismatched and unknown receipts cannot be promoted by a bootable
looking kernel/loader. Audit digests must resolve to retained independent evidence.

Synthetic EvidenceComplete means the **evidence contract** is complete. It is not
production support, bootability, authorization, RecoveryReadiness or rollback.
No production receipt collector/signer or automatic reboot gate is supplied. The
Linux durable sink is implemented as described below; there is no blind retry after
any partial receipt.

## Remaining implementation and disposable-VM gate

Before accepting the architecture: qualify the offline builder against the complete
GNOME+hardware package closure, implement the controlled native namespace/device/firmware
backend and observer, finish target-root operation implementations and the exact agent worker, qualify the signed
loader profile, permanent NVRAM journal/finalizer and durable Linux receipt sink.
These are implementation gaps, not merely tests awaiting execution.

Then validate on a WIN11-BASE disposable clone with Secure Boot and two ESPs on the
same GPT disk. Retain prepared receipts, independent inventory and Windows ESP
content baseline. Disable installer network and verify the complete workstation
deploys from the authenticated bundle. Reverse enumeration and test duplicate,
missing and changed identities. Test every package hook/trigger with Windows ESP
still qualifying, inspect all actual writes and firmware before/after, and verify
no other ESP/filesystem/variable changed. Inject power/process/I/O failure at
bootstrap, apt, kernel/initramfs, signed-file copy, firmware, agent, journal and
unmount boundaries; require truthful partial/unknown evidence and no blind retry.
Verify target identities, accounts/GNOME/networking, new machine identity, initramfs,
signed EFI files, final GRUB references and reopened complete receipt. Reboot only
after explicit lab approval; test automatic Debian launch, subsequent reboot,
Windows launch/preservation and the actual migration agent. Restore VMware
checkpoints separately; that is not product rollback validation.

No real install, mount on developer storage, firmware/BCD/RTC write, reboot or
disposable-VM execution was performed in this continuation. Exact validation
counts are recorded in [the phase log](../fleet/phase-2.md).

## Debian content continuation — 2026-09-28

This section supersedes earlier implementation-gap descriptions where explicitly
noted. It does not accept debootstrap as the complete production strategy.

### Authenticated offline source

`OfflineDebianPackageSetV1` binds preparation generation, build generation, policy
hash, architecture, keyring identity, APT version, debootstrap identity, repository
origins/suites, original InRelease files, signed index hashes, package filenames,
versions, lengths and SHA-256. The source embeds this set in the immutable deployment
plan, so checkpoints/receipts bind the same source generation. C# structural validation
is deliberately not an authenticity or dependency-satisfaction assertion.

`native/offline_bundle.py` implements build and independent verify commands. It uses
[gpgv](https://manpages.debian.org/trixie/gpgv/gpgv.1.en.html) with a separately trusted,
hash-pinned archive keyring and allowed signing fingerprints; checks signed codename,
architecture, Date/Valid-Until and a bounded metadata-age policy; verifies original
compressed index hashes; and checks every selected archive against its signed package
record. Modified metadata, wrong keys, wrong architectures, missing files, changed
bytes and symlinked artifact paths fail closed. No trusted=yes, signature bypass,
ignored missing package or live-mirror fallback exists at deployment time.

The dependency solution is produced and independently repeated by Debian's
[python-apt API](https://apt-team.pages.debian.net/python-apt/library/apt_pkg.html),
using an empty dpkg status, isolated configuration/cache, file-only signed sources,
and no commit/install call. Required product packages, the authenticated candidate
priorities required/important/standard and debootstrap's actual minbase selection
are inputs. Recommends are enabled, Suggests disabled; unresolved recommendations
also reject the solution. This is not a handwritten dependency resolver.

`debootstrap --print-debs` confirms the base selection and `--download-only` obtains
its actual archives without installation. These are
[documented operations](https://manpages.debian.org/trixie/debootstrap/debootstrap.8.en.html).
Base-suite archives are retained separately from the final APT security/updates
solution: requiring a newer final version does not make an older minbase archive
magically available offline. Both sets must verify. Acquisition from mutable Debian
repositories can fail if metadata changes mid-build; there is no silent re-resolution
or overwrite/resume. Rebuilding requires the retained metadata and artifacts, not
just the word `trixie`.

The build request is an external trusted input containing Release/Architecture,
GenerationId/BuildId, PolicyPath/PolicySha256, KeyringPath/KeyringSha256, allowed
SigningFingerprints, DebootstrapPath/DebootstrapSha256, independently obtained
BootstrapPackages, MaximumMetadataAgeDays (1–90), and the three repository IDs:
`debian`/trixie, `debian-updates`/trixie-updates and
`debian-security`/trixie-security, with HTTPS origins. The original metadata layout
and `pool/` filenames are retained under those directories. `package-set.json` and
`debian-archive-keyring.gpg` sit at bundle root. Verification additionally requires
the expected manifest SHA-256 from outside the bundle. The request/keyring/policy
must not be trusted merely because they are present beside an untrusted bundle.

**Not yet proven:** a complete real Trixie GNOME bundle build/download/independent
verification and offline bootstrap/install rehearsal. The runtime toolchain,
including debootstrap helper scripts, APT/python-apt, gpgv and trust-anchor lifecycle,
still needs an immutable qualified runtime profile and production source observer.
Tests use an ephemeral signing key and tiny synthetic Debian repositories. The
WSL fixture APT is 2.8.3, not evidence that Trixie APT 3.0.3 executed a workstation
installation. No Debian archive authenticity claim is made from those fixture tests.

### Workstation and legacy-policy translation

`DebianWorkstationPolicy.Trixie` is deterministic: amd64 kernel/meta and initramfs
tools; `task-gnome-desktop`, GDM, NetworkManager, sudo; locale/keyboard/timezone,
systemd/dbus and standard tools; bounded firmware-linux/iwlwifi/realtek baseline;
Python/GI/rsync/NTFS tools; Debian archive trust, signed shim/GRUB support and
efibootmgr/mokutil. Full package names are in the immutable policy. `standard` is a
task selection translated from priority metadata, never a package named
`task-standard`. Every actual install command is version-pinned and uses only the
file-backed bundle; final online update sources are intended installed-system state,
not migration transport. Hardware needing drivers outside this baseline, DKMS/MOK
or another firmware package set is **not universally supported** by the baseline.
It needs an explicitly qualified hardware extension before migration; optional
applications/codecs can remain an explicit later online responsibility.

| Legacy behavior | New disposition |
| --- | --- |
| GNOME, GDM, NetworkManager, sudo, intended user/locale/timezone | Required product state, declared and independently checked |
| Live squashfs copying, hd-media, iso-scan, partman biggest_free/atomic/default bootdev | Obsolete installer transport/ownership; no production export path |
| No migration network | Preserved; source authentication and complete offline closure precede consumption |
| Final Debian update sources | Required installed configuration; isolated from deployment APT configuration |
| First-boot password repair and broad Windows/OEMDRV discovery | Rejected; protected stdin credential contract and exact payload/ownership required |
| Broad GPU discovery, upstream repository injection, boot cleanup, os-prober | Not inherited; hardware qualification and a separate reviewed first-boot policy required |
| Optional applications/codecs and actual user-data migration | Explicit later responsibilities, not silently marked complete by installing a service |

### Native stage boundary, configuration and kernel evidence

The existing 43-stage engine is retained. `DebianNativeDeploymentOperations` requires
an embedded bound offline set, freshly asks its host for inventory/mount/isolation
readback, and compares that with the freshly resolved devices supplied by the engine.
It dispatches typed configuration, mount, package and agent operations; unsupported
stages throw before a command is sent. There is no generic local Process.Start
fallback for package or filesystem deployment. Signed packages, loader writes and
firmware finalization remain unsupported commands.

`IDebianIsolatedStageHost` is the missing privileged broker, not an implemented
sandbox. It must enforce private mount/PID/network namespaces, deny preserved and
raw-block writes, deny firmware writes and mount escalation, and exclude host service
sockets. It must independently verify those effective restrictions and exact mounts
before each command, including commands in a multi-command stage. The stage runner's
reopen check is not proof those kernel restrictions exist. Linux root, ESP and payload
mounts remain the existing canonical shared contract; Windows ESP must not be mounted,
and unexpected second ESPs, binds, symlinks, parent/type/UUID/generation changes fail.
**These are enforced pure checks and broker requirements, not a claim of native
namespace enforcement. No such backend is supplied in this continuation.**

The dispatcher connects hostname, locale, timezone/keyboard, networking, fstab,
machine-id and package policy declarations to exact target-file operations. Temporary
policy-rc.d and os-prober policy precede package configuration. Configuration packages
precede locale execution. User creation, encrypted password stdin, root locking and
sudo membership are separate explicit calls. `IDebianCredentialInput` may send a
verified protected credential only through stdin, never argv, logs or receipts.
The native credential provider, safe no-symlink file writer, archive-keyring copy,
effective account readback and final temporary-source/resolver/service-policy cleanup
still require implementation. A closure including openssh-server is blocked until
its target host-key lifecycle is qualified; installer keys are never copied.

`DebianKernelEvidence` requires the exact configured kernel package/version,
non-symlink kernel and modules.dep artifacts, and the expected initramfs/kernel
relationship. Generation targets the single declared kernel release, not the running
installer kernel or every kernel discovered at execution. Exit zero alone is
insufficient; a failed command can retain present artifacts as partial evidence.
Native acquisition of these artifact observations remains part of the missing observer.

### Agent and receipt boundaries

`DebianAgentProfiles` declares only exact root destinations:
`/usr/lib/igloo/debian-agent.py`, generated secret-free
`/etc/igloo/deployment.json`, and `/etc/systemd/system/igloo-deployment.service` plus
its exact enable link. Root-owned modes are declared; the unit uses DynamicUser,
private devices/network, no capabilities, protected system/home, and inaccessible
boot/firmware paths. It is enabled, never started during deployment. Configuration
does not copy the raw migration manifest or credentials. File hashes, modes, unit
content and enablement must be read back independently.

**The worker is not qualified or implemented as a replacement for all required
Community migration behavior.** The native dispatcher refuses InstallAgent, and
the Debian plugin cannot return the legacy agent. An installed service would mean
InstalledFirstBootPending, not user-data migration/enrollment complete. The new
profile must also be connected to final agent receipt paths/hashes when that worker
exists; the historical candidate receipt mapping is not a production completion path.

Command evidence records generation, plan/stage, executable and tool hash, public
operation hash, start/completion and exit code. Raw stdout/stderr/stdin and exception
messages are not persisted. The runner still writes/reopens intent before the effect,
independently inspects afterward, retains partial checks and stops on Failed or
OutcomeUnknown. The new PreBootStagesVerified checkpoint means only the verified
prefix before ConfigureSignedPackages; later identity/initramfs, boot and teardown
stages still remain. It does **not** mean content complete or EvidenceComplete.

The Linux journal opens an already-private owner-only directory, rejects symlink and
hard-link substitutions, exclusively reserves generation, and uses create-new 0600
checkpoint files with their SHA-256 in the reference. It fsyncs files and directories,
verifies earlier checkpoint hashes before append, and reopens using another process.
Fsync failure is failure even if the file exists. No cleanup, overwrite, resume,
rollback or retry command exists. Deployment-filesystem power-loss tests remain pending.

`DebianBootFinalizationHandoff` binds exact root/Linux ESP/Windows ESP, preparation
generation and plan, offline package build, signed shim/GRUB source versions and
the `/EFI/debian` paths. Hook and firmware observations retain their explicit
Unsupported/Unavailable states. This is desired finalizer input; neither declaring
it nor hashing it grants finalizer support or authorizes a write.

### Precise remaining blockers

1. Qualify a full authenticated Trixie GNOME/hardware bundle, complete runtime
   toolchain and independent production source observer; fixture signatures/solver
   tests are not that bundle.
2. Implement and validate the native Linux isolation/mount/file/credential/observer
   backend, remaining configuration cleanup and stage evidence acquisition. A chroot
   or mount namespace alone is insufficient.
3. Implement a reviewed exact first-boot migration worker and bind its dedicated
   profile to final agent receipts. No legacy cleanup/discovery fallback.
4. Qualify signed loader/package hooks, their indirect firmware behavior, permanent
   NVRAM intent/journal/finalization and readback.
5. Execute the complete offline workstation/failure-injection lifecycle in disposable
   Debian VMware clones, including Windows ESP content preservation and reboot evidence.

Consequently the answer to “are only bootloader/Secure Boot/NVRAM and VM evidence
left?” is **No**. Production preparation/deployment/registration stays blocked.

## Debian non-firmware continuation — 2026-09-28

**DEBIAN NON-FIRMWARE FOUNDATION BLOCKED.** This section supersedes the preceding
implementation checkpoint, without accepting or enabling production deployment.
No Mint/Fedora integration, Windows preparation, boot registration, permanent
firmware finalization, RecoverySnapshotV1, RecoveryReadiness or PreCommitGate was
changed in this continuation. No target-root deployment was run on host storage.

### Real signed repository result

[Retained acquisition evidence](debian-nonfirmware-evidence.json) records a real
download/read-only audit, not a synthetic repository:

- Preparation test generation `284423f2-c1dd-497e-8815-320af0418def`; attempted build
  `2a35782f-a4f6-4bf2-9ddb-76e577147473`. These are audit identities, not an actual
  prepared developer disk or a completed production bundle.
- Policy SHA-256 `6C31CA82B7F3E88E9E428B8857D6822FC5A888E9D2390EB6F806801004C45AFF`.
  Trusted keyring SHA-256
  `59E6105598E3D9924929553293AC7F283D342EF60C5FAA009FC0A3C6542A128A`.
- Trixie, trixie-updates and trixie-security InRelease signatures were checked with
  gpgv and a separate keyring, with exact allowed fingerprints taken from the
  [Debian FTP key publication](https://ftp-master.debian.org/keys.html). Both current
  Trixie and still-active Bookworm archive signing keys are included: the actual
  stable InRelease has multiple signatures. Trust anchors were fetched separately
  over HTTPS and fingerprint-pinned; trust was not taken from the candidate bundle.
- Twelve signed amd64 component index hashes were checked. The evidence retains
  source origins, metadata/index hashes and actual signing fingerprints.
- The actual authenticated debootstrap **1.0.141** selected **78 minbase packages**.
  Their real `.deb` bytes, versions, architectures, filenames and hashes were checked
  against authenticated indexes and independently reread in another process.
- The full GNOME solver selected **1,594 packages but REJECTED the closure**:
  `gvfs-backends 1.57.2-2+deb13u1` recommends `wsdd`, which is absent from the inspected
  signed repositories. Recommends remain enabled and Suggests disabled. No exception,
  `wsdd2` substitution, older-release package or live-mirror fallback was introduced.
  This agrees with [Debian bug 1110689](https://bugs.debian.org/1110689).
  Another fresh process reproduced the rejection.
- **No complete GNOME bundle or package-set manifest was emitted.** The 1,594 count
  is a rejected solver selection, not downloaded/qualified workstation packages.
  The audit directory is `/tmp/igloo-241-real-bundle-09s3zdel` in Ubuntu-24.04 WSL.
  Two extra archives from the initial HTTPS transport investigation remain there;
  they are not members of the verified 78-package offline minbase set. No audit
  directory is accepted as a production source merely because it exists.

Real acquisition found and fixed a builder defect: `--print-debs` against a file
mirror selected 78 packages, while `--download-only` against HTTPS added
ca-certificates/openssl and refetched live metadata. Both operations now consume
the **same captured file repository**. Archives are first fetched by authenticated
metadata identity, then debootstrap's actual downloaded selection is verified.
Ambiguous base-suite versions fail instead of selecting one heuristically.

The builder now reports public unresolved-recommendation identities and rejected
package count. It still refuses to create the success manifest. The next package
policy decision needs an upstream fix or an explicitly reviewed product policy;
it must not silently drop this recommendation.

### Runtime identity, not runtime qualification

`native/runtime_profile.py` independently checks declared executable hashes and
installed package versions, and hashes the complete debootstrap helper tree,
including confined suite symlinks. A substituted helper, tool or profile fails.
The trusted request pins the observer module itself. An optional runtime profile
hash is retained by OfflineDebianPackageSetV1 and its durable codec; the native
dispatcher now refuses a set without that binding.

The observed **build host**, not a qualified deployment image, has APT **2.8.3**,
python3-apt **2.7.7ubuntu5.2**, Python **3.12.3-0ubuntu2.1**, dpkg
**1.22.6ubuntu6.6**, gpgv **2.4.4-2ubuntu17.6**, and the separately extracted
authenticated debootstrap **1.0.141**. The captured profile file SHA-256 is
`E29CC7D3CA9F5B4B633AE9D22B61B493214C09375154F70D2B061F23C1A4580C`.
The helper-tree hash is retained in the audit. This does **not** qualify a Trixie
runtime ABI, all transitive libraries, the deployment image, or a production source
observer joined to a real PreparedLayout. Full toolchain packaging and qualification
remain blocked with the complete workstation bundle.

### Native primitives implemented and tested

`native/target_files.py` operates relative to a pinned root directory FD with the
expected filesystem device and mount ID. Linux `openat2` uses RESOLVE_BENEATH,
RESOLVE_NO_SYMLINKS and RESOLVE_NO_XDEV. Unsupported kernels fail. Only configuration
namespaces `/etc`, `/usr` and `/var` are admitted; `/boot`, ESP, payload, `/dev`,
`/proc` and `/sys` writes are unavailable. Parent directories must already exist.
Files must have one link. Exact before-state is compared, writes use new files and
atomic rename, owner/mode and file/directory fsync, followed by a fresh independent
open. Exact symlink objects can be created/removed without following them. Fsync
failure retains partial evidence, never success or automatic cleanup/retry.
This is a file primitive, **not authority to choose a root**: the missing broker
must provide canonical mount validation, exclusive/quiescent ownership and prevent
concurrent rename/mount substitution. The tests use disposable ordinary directories.

`DebianSealedCredentialInput` implements IDebianCredentialInput for Linux. It owns
a close-on-exec duplicate of a supplied memfd and requires SEAL, SHRINK, GROW and
WRITE seals. A single-use SHA-256-bound `$6$`/`$y$` encrypted credential is validated,
sent only as `username:crypted-value` on stdin, and temporary buffers are zeroed.
No default password, argv secret, receipt secret or output logging exists. The
protected descriptor producer and native isolated command connection are still
required. The C# implementation is compiled/tested for platform refusal; Linux
fixtures exercise the real kernel seal/write-rejection contract. End-to-end C#
memfd/chpasswd execution has **not** been qualified in a Debian runtime.

`native/target_observer.py` adds fresh dpkg database queries (no maintainer scripts),
regular boot-artifact size/hash/type/owner/mode observations, and mountinfo/device
number/path acquisition for the existing shared resolver. Configuration content,
mode, owner, exact link and absence readback use the file primitive. No mutating
process stdout becomes evidence. These primitives are not the complete 43-stage
observer: accounts/sudo, service state, locale semantics, initramfs members, full
Windows preservation, and effective isolation integration still need acquisition.

### Why the isolation broker and real rehearsal remain blocked

There is **no production implementation of IDebianIsolatedStageHost**, native
canonical mount/unmount supervisor, or verified device/capability policy. A harmless
unshare user/mount/PID/network probe running only `/usr/bin/true` succeeded in WSL;
this is not proof of deployment isolation and did not mount target storage.

The authenticated debootstrap functions expose a concrete incompatibility to
resolve. `check_sane_mount` attempts mknod and then a bind mount; `setup_devices`
can fall back to bind setup; `setup_proc` attempts mounts/rbind and can ignore
some failures. The current single Bootstrap stage cannot be given arbitrary mount
or device privileges and then treated as restricted package execution. No internal
CONTAINER flag, patched helper script, full host `/dev` bind, or fakechroot shortcut
was used to conceal this issue.

The required design remains a trusted mount supervisor, separate from target package
processes, plus independently verified namespace/device/capability restrictions.
Target commands should receive only required pseudo devices and no raw block
devices; only the supervisor may open exact resolved root/Linux ESP/payload devices.
Preserved Windows devices, firmware, mount escalation, host sockets and deployment
network must be unavailable. The supervisor also needs exact reverse unmount and
busy/unavailable evidence, without force/lazy success claims. Those restrictions
are requirements, **not implemented enforcement**. No real debootstrap/apt install,
loopback deployment, privileged mount, or developer firmware operation was attempted.

### Restricted first-boot worker and receipt integration

`native/debian_first_boot.py` is now an actual replacement **evidence worker**, with
its exact bytes embedded and hashed by DebianFirstBootEvidence. It does not execute
the legacy agent. It validates protected fixed-path configuration, its own payload,
generation and exact required completion receipt references; writes/fsyncs/reopens
create-new intent and outcome files; and refuses retry when intent already exists.
It has no subprocess, network, mount, firmware, boot cleanup, partition discovery,
service-control, user-home modification or general cleanup operation.

Required receipt kinds are DeploymentContent, UserData and Enrollment. A missing,
changed, partial or wrong-generation required receipt fails. The worker consumes
evidence from separate producers; it does **not** manufacture those producers'
success. The privileged exact user-data import producer and any required enrollment
handoff are still unimplemented. Therefore the broad migration-worker qualification
and InstallAgent production dispatch remain Unsupported. Omitting mandatory user
work is not an accepted way to use the narrower worker.

The profile retains DynamicUser, private devices/network, empty capability set,
ProtectSystem/ProtectHome and inaccessible `/boot`/firmware; it adds restricted
address families and mount/raw-I/O/reboot syscall groups. No start during deployment.
The worker handles only systemd's documented exact DynamicUser StateDirectory link
to `/var/lib/private/igloo-deployment`; arbitrary symlinks remain rejected. Actual
Trixie systemd unit execution and id-mapped StateDirectory behavior require VM proof.

DebianInstallationReceiptV1 can now carry exact installed worker/config/unit hashes,
file ownership/modes, enablement and these states:
InstalledFirstBootPending, FirstBootSucceeded, FirstBootFailed,
FirstBootOutcomeUnknown. Outcome bytes have their own evidence hash. New-profile
receipts use generated config/unit identities rather than the raw legacy manifest.
Pending mandatory first-boot work cannot become evidence-complete migration. Legacy
fixture serialization is retained; the legacy production plugin stays unreachable.
The outcome also binds systemd's invocation ID and requires independently observed
unit exit/result evidence. A success-looking file from a failed/unknown invocation
cannot promote the receipt. Intent/outcome publication uses file fsync before
create-new publication and directory fsync afterward; errors retain uncertainty.

Machine identity now declares an **empty** `/etc/machine-id`, with the D-Bus link
pointing there. This follows the documented image initialization behavior in
[Trixie's machine-id(5)](https://manpages.debian.org/trixie/systemd/machine-id.5.en.html):
systemd generates an ID without interpreting the image as ConditionFirstBoot=yes
and applying first-boot presets. The worker has no ConditionFirstBoot dependency.
No runtime machine ID is copied. Final identity declarations also remove temporary
offline APT/service/resolver policy and install the final update sources; native
application/readback remains broker-dependent. SSH-server closures still require
an explicit host-key lifecycle and remain blocked.

### 43-stage support accounting and next blockers

No additional **end-to-end native deployment stage** is claimed supported. The
existing ordered protocol, durable intent/reopen and failure rules are unchanged.
New executable primitives support pieces of VerifySourceTrust, configuration and
identity stages, ConfigureUser, InstallAgent and independent inspection. All still
require the missing canonical isolated host/observer integration. Unsupported
dispatches remain unsupported before invocation.

| Protocol stages | Current native limitation (beyond the pure stage contract) |
| --- | --- |
| ValidateOwnership, ResolveRuntimeDevices | Existing canonical collector/resolver; no complete production Debian observer/session supplying verified target context |
| PrepareNamespace, MountRoot, MountLinuxEsp, MountPayload, ExcludeWindowsEsp, MountHelpers | Privileged supervisor, effective restriction proof and exact native mounts absent |
| VerifySourceTrust | Real signature/index/minbase primitives work; full GNOME closure rejected and production PreparedLayout/runtime binding unqualified |
| Bootstrap | No qualified restricted setup/package-process split; no installed target-root readback |
| ConfigureApt, ConfigureHostname, ConfigureLocale, ConfigureTimezoneKeyboard, InitializeMachineIdentity, ConfigureNetwork, GenerateFstab, FinalizeMachineIdentity | Exact declarations and file primitives exist; isolated session, safe parent creation, semantic configuration observers and cleanup orchestration remain absent |
| ConfigureArchiveKeyring | Authenticated source exists; exact binary-copy and independent target trust readback not wired |
| ConfigurePackagePolicy, InstallKernel, InstallFirmware, InstallDesktop | Pinned offline instructions exist; complete package source, package-hook isolation, semantic package/kernel/modules/initramfs readback and native execution not qualified |
| ConfigureUser, ConfigureSudo | Sealed credential consumer exists; protected producer, isolated process connection, account creation/group/credential readback missing |
| InstallAgent | Evidence worker/profile exists; required privileged UserData producer and native file/service/unit readback not qualified; dispatcher still refuses |
| ObserveFirmwareBeforePackages, ConfigureSignedPackages, DrainPackageTriggers, FinalizeLoaderFiles, GenerateGrubConfiguration, InspectBootFiles, ObserveFirmwareBeforeFinalization, FinalizeFirmware, InspectFirmwareAfterFinalization | Existing boot/hook/finalization blockers retained; no firmware write implemented |
| GenerateInitramfs | Exact kernel instruction and regular-file observation exist; command isolation and independent initramfs member/kernel correlation acquisition unqualified |
| VerifyWindowsPreservation | Canonical pure checks exist; native before/after content and package-target exclusion observer incomplete |
| PersistDeploymentEvidence, PersistCompletionEvidence | Native journal fsync/reopen exists; complete independent receipt producer/readback not wired into a native session |
| UnmountHelpers, UnmountLinuxEsp, UnmountPayload, UnmountRoot | Native exact reverse-order unmount supervisor and busy/failure readback absent |

Non-firmware blockers are now precise:

1. Resolve the authenticated Trixie `gvfs-backends -> wsdd` policy gap without
   silent omission; build and independently verify the entire package set; qualify
   the complete Trixie runtime and production source observer.
2. Implement/qualify the trusted mount supervisor and package sandbox, including
   debootstrap setup separation, exact device deny policy, all native observers,
   credential producer/connection, safe parent-directory/keyring operations and
   reverse unmount. The standalone file/credential/readback code is not that host.
3. Implement the privileged exact user-data completion producer and mandatory
   first-boot handoffs, then qualify the evidence worker/unit and full receipt path.

Signed-loader/package-hook/permanent NVRAM finalization remains a separate untouched
blocker; disposable VMware lifecycle and failure-injection validation also remain.
**It is still false that only firmware work remains.** Native Windows preparation,
deployment, registration and reboot stay disabled. The developer host was not used
for an actual installation.

### Validation for this continuation

All requested project tests ran with `--no-restore -m:1 -warnaserror`:

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Core | 330 | 0 | 0 |
| Preflight | 343 | 0 | 0 |
| Community.App | 58 | 0 | 0 |
| Migration | 257 | 0 | 0 |
| Debian filter (subset of Migration) | 226 | 0 | 0 |
| Linux/native fixtures | 66 | 0 | 0 |

`dotnet build -warnaserror`: **0 warnings, 0 errors**.
Full `dotnet test Igloo.sln --no-build --no-restore -m:1`: **1,177 passed,
0 failed, 0 skipped**, including 19 Iso, 23 UsbWriter and 147 Fleet tests.
This adds **22 .NET** and **32 Linux** tests over the prior checkpoint.
`git diff --check` passes; existing LF/CRLF notices are not whitespace failures.
New untracked files were also inspected for trailing whitespace.

Real validation was limited to signed repository/minbase readback, a harmless empty
namespace process, and temporary-file/seal fixtures. There was no real target-root
install, package maintainer-script execution, systemd worker unit qualification,
firmware write, reboot, disposable VM migration, commit or push.

## Native isolation continuation — 2026-09-28

**DEBIAN ISOLATION FOUNDATION BLOCKED.** The detailed
[execution-boundary record](debian-isolation-boundary.md) supersedes the earlier
statement that no native sandbox/mount mechanics exist. It does **not** supersede
the production gate or certify a complete IDebianIsolatedStageHost backend.

Implemented: typed plan-bound launch declarations; a native package/configuration
broker using authenticated Trixie bubblewrap 0.12.0; a trusted static setup/gate;
sealed seccomp policy; exact capability reduction; fresh independent effective
policy observers; and FD-anchored mount/unmount mechanics with readback and failure
poisoning. The child has private mount/PID/network/IPC/UTS views, minimal pseudo
devices, no raw devices or setup FDs, read-only ESP/source, empty read-only sysfs
and no host service sockets. Intent and a second observation precede tool release.

Native directory/tmpfs fixtures passed; real GPT/FAT32 mounts, complete Debian
runtime, debootstrap and production session/journal composition remain unqualified.
The authenticated debootstrap helper audit confirms the second stage still performs
device/mount setup before dpkg; no patch, ignored setup failure or privilege grant
to maintainer scripts was accepted. Bootstrap remains Unsupported.

The record includes two corrected native findings (bounding set and propagation)
and one rejected detached-FD experiment. That experiment never released a target
command but created two empty WSL setup placeholders; both were inspected, removed
individually and independently verified absent. No Windows ESP or firmware changed.
The detached-FD import route was removed, not tolerated by the verifier.

No production protocol stage is newly certified. Remaining isolation work includes
canonical block lease/session integration, a verified root-only package view,
bootstrap privilege separation, full runtime qualification, protected-input wiring,
semantic observers and durable journal composition. The `gvfs-backends -> wsdd`
closure blocker and first-boot producers were not changed by this task.

Validation: Core **330**, Preflight **343**, Community.App **58**, Migration **275**
passed with `--no-restore -m:1 -warnaserror`; the Debian filter passed **244**
(Migration subset). Linux deterministic/native primitives: **109 passed**.
Explicit privileged directory/tmpfs broker rehearsal: **36 passed**. No failures
or skips. Added **18 .NET**, **43 deterministic Linux** and **36 explicit native**
tests. Solution build has **0 warnings, 0 errors**. Full serialized solution:
**1,195 passed, 0 failed, 0 skipped**. `git diff --check` exits **0**; existing
LF/CRLF notices are separate from whitespace failures. New untracked files were
also checked for trailing whitespace. No analyzer suppression, commit or push.

## Canonical session continuation — 2026-09-28

**DEBIAN SESSION FOUNDATION BLOCKED.** The
[session implementation and 43-stage support matrix](debian-session-boundary.md)
record the new boundary and its remaining limitations.

Core now derives canonical root/ESP/payload lease declarations from the existing
complete ownership resolver. Debian's single-use authority revalidates every fresh
inventory challenge and uses the existing Linux durable journal for session
intent/result records. The persistent native supervisor opens only declared block
FDs and connects mount/readback/unmount callbacks to that authority over private
pipes. Only trusted supervisor code receives raw block FDs. A rejected observation,
replay, failed checkpoint, substituted descriptor or stale mount stops the session.

This is not yet a qualified IDebianIsolatedStageHost. Missing pieces include real
GPT/EXT4/FAT32 qualification, protected mountpoint creation, a connected root-only
package view, old-versus-restricted helper contract reconciliation, bootstrap
privilege separation, complete semantic observers, credential/producers and the
full stage-journal composition. Zero stages are newly NativeSupported.

Actual read-only WSL acquisition returned `Unavailable / sfdiskExit1`; no lease
was fabricated. No GPT image or block-mount/debootstrap/apt rehearsal was attempted.
Real native tests use only temporary directories/private tmpfs. They include fresh
persistent-supervisor startup/close, independent namespace observation and journal
reopen, rejection before a block open, and rejection of an actual detached clone
before bubblewrap. Temporary fixture trees were removed; the two old WSL-root
placeholders remain absent. No developer disk or firmware state was modified.

Stock authenticated debootstrap 1.0.141 still requires its mknod/bind-mount check
and second-stage setup. No supported redundant-setup skip or capability-drop
transition was established. No script patch, fabricated container flag, widened
package privilege, ignored failure or fakechroot shortcut was introduced.
GNOME's retained `gvfs-backends -> wsdd` closure result and privileged first-boot
producer blockers are unchanged. It is not true that only firmware remains.

Final validation: Core **353**, Preflight **343**, Community.App **58**, Migration
**293**, Debian filter **262** (subset), Linux deterministic **132**, explicit
native **40** passed; **0 failed, 0 skipped**. The requested targeted .NET runs used
`--no-restore -m:1 -warnaserror`. Full serialized solution: **1,236 passed**, including
19 Iso, 23 UsbWriter and 147 Fleet. Solution build: **0 warnings, 0 errors**.
This adds **41 .NET**, **23 Linux deterministic**, and **4 explicit native** tests.
`git diff --check`: **exit 0**, with 28 existing LF/CRLF notices recorded separately;
new/edited untracked files also have no trailing whitespace.
No analyzer suppressions, package upgrades, commit or push.

## Selected-document UserData producer fixture — 2026-09-30

The producer now exists separately from the restricted evidence worker. The real
shared `MigrationManifest.Files` / `MigrationFolder` selection maps each selected
`sourceRelativePath` tree to `<home>/<name>`. `DebianUserData.Plan` exports that
mapping and an exact content inventory; `userdata_producer.FixtureContext` checks
explicit fixture directory leases, account/home identity, complete selected trees,
source content, collisions and capacity. A manifest digest alone grants no access.
`perform` reserves a new operation, durably records/reopens intent, copies through
existing `target_files.beneath`, and invokes a fresh `userdata_entry --observe`
process. `--reopen` independently validates the terminal producer journal sequence
and the existing five-field UserData envelope. Producer records v2 bind both the
observation and result to the exact intent hash/reference and source/account context.
A later failure invalidates an earlier success-looking record. The first-boot worker
only consumes receipts; its envelope remains v1.

Supported scope is `SelectedDocumentTreesV1`: nonempty, complete selected ordinary
file/directory trees from the six existing product folder choices; at most 1,024
entries and 32 MiB selected bytes. Paths have at most 16 components and 512
characters, with NFC-normalizable Latin-1 names and simple invariant uppercase
collision checks. Spaces and accented names are covered. Every selected top-level
destination must be absent (`CreateNewSelectedTrees`); there is no merge, overwrite,
cleanup or source mutation. Files receive UID/GID of the verified account and mode
0600; directories receive 0700. Source permissions are not privileged metadata to
restore. Complete source and destination fixture snapshots are separately bounded.

Browser profiles, wallpaper/account-picture transfer, arbitrary Unicode outside the
stated name policy, links, hardlinks, special files, xattrs/ACLs/capabilities, set-ID
metadata, streams and protected account/key paths are unsupported. Required work is
rejected, never silently skipped. These Linux fixtures do not supply Windows/NTFS,
reparse-point or EFS acquisition evidence. Such sources cannot enter this fixture
composition as a Windows migration. The legacy agent/staging copier is not invoked.

The native acceptance transferred four selected files (313 bytes), including nested,
empty, binary, spaced and Unicode examples. Independent observation verified every
selected destination, UID/GID 1000, safe modes, unchanged source and unrelated target
sentinel. Three durable records and the receipt were reopened in fresh processes.
The actual native result passed the rebuilt .NET fixture-result decoder. Both stores
and test accounts are explicitly synthetic; no first-boot configuration was installed
and no DeploymentContent, Enrollment or FirstBootSucceeded result was manufactured.

See [new evidence](debian-userdata-producer-evidence.json) for the executed hashes,
commands, reservations, retained runtimes and raw result references. Both allowed
native acceptance passes were used. The first verified the files but lacked an explicit
intent hash-link in its receipt evidence; it is retained without final qualification.
The corrected v2 producer passed the second run on fresh resources, including the
rehashed-intent substitution regression. Fault-injection fixtures are separate evidence.
Directory leases closed, tools media was ordinarily unmounted with fresh absence, and
runtime power-down was observed separately. No canonical target/backing was attached.
The original runtime's full hash matched after shutdown. No new power-loss claim is made.

The next integration boundary must supply a validated selected-source acquisition
context and an explicitly authorized destination account/home through the canonical
owned session, then admit only the exact completed scope's independently reopened
producer evidence into the protected first-boot input store. This task implements
only the named fixture composition, not production source authority. The envelope is
structurally compatible but MUST NOT be installed as production completion evidence;
its bound result says `FixtureOnlySelectedDocuments`. Missing mandatory producers still
block first boot. Agent installation, Enrollment, canonical UserData orchestration and
real user migration qualification remain pending. ProductionAuthentication stays
Unsupported and NativeSupported stays 0; Windows/recovery/readiness gates are unchanged.

Failure semantics apply to the transfer operation: `NotStarted` means no target
content effect was released, even when a reservation or intent already exists.
After a possible directory/file effect, unsuccessful observation or persistence is
`OutcomeUnknown`; there is no automatic retry, cleanup or rollback claim. The exact
terminal chain must be reopened, so an earlier result followed by failure cannot
be admitted as completed UserData. Fixture shutdown is not canonical teardown.


## Canonical selected-document successor implementation - 2026-09-30

Current execution status: see [the 2026-10-01 attempt below](#canonical-userdata-execution---2026-10-01).
The capacity block and unused budgets described in this historical section have been superseded.

Current status: implemented and contract-tested, **not native-qualified**. This
supersedes missing-wiring statements above only for the development lab composition.
Earlier fixture evidence remains unchanged. See
[canonical continuation evidence](debian-userdata-canonical-evidence.json).

`DebianVerifiedInitramfs.Reopen` consumes successful initramfs and exact Close chains.
`ContinueLabInitramfsCheckpoint` preserves original preparation lineage and validates
independent copies under a new external one-use
`OneInitramfsCheckpointSelectedDocuments` authorization. Initial copy equality and
later payload staging have distinct records and hashes; no new format receipt exists.

`LabUserData.RunAsync` connects shared inventory, ownership/leases and actual journal
placement verification to `ForLabUserData`, `DebianNativeMountSession` and
`session_userdata.perform`. Provider version 5 retains lab origin and the closed
`SelectedDocumentTrees` scope. Earlier scopes cannot acquire `TransferUserData`.
The wire retains original numeric lease roles/access and existing Windows semantics.

`canonical_userdata_guest.stage_documents` is separate provisioning on the newly
copied FAT32 payload. It writes only `userdata/<operation>/source`, independently
reopens its content and compares unrelated artifact objects. Host finalization occurs
after staging shutdown and rehashes the changed working target. These primitives
have not executed here. During canonical transfer the acquired payload is read-only;
runtime/journal/tools media are not alternative sources. Logical OneDrive naming
proves no Windows, NTFS, cloud, reparse-point or EFS acquisition.

The private-pipe authority declaration and connected root/payload views construct a
separate canonical context. `userdata_source.observe` verifies the staged inventory,
current target account database and owned home. The existing copier and independent
observer remain shared. The full-root observer composes the original artifact,
verified v5 changes and exact predecessor initramfs with only declared additions.
The OS filesystem is not subject to the selected transfer's 1,024-entry bound.

Transfer and admission have separate durable intents and reopened results.
`userdata_admission` derives `/var/lib/igloo/first-boot-input` from the worker contract
and creates only `userdata.json` and `userdata-evidence.json` (root:root 0644;
new protected directories 0755). The bundle carries exact producer records, transfer
and authority bytes, without dependence on a developer-host path. The v1 envelope
alone is insufficient: `ValidateLabAdmissionStructure` and `validate_admitted_bundle`
check v2 terminal/intent/context bindings and explicit lab scope. Neither is production
authority. No worker configuration, DeploymentContent, Enrollment or FirstBootSucceeded
is created. Later first-boot composition must retain this limited provenance and
must not substitute it for broader mandatory migration work.

`canonical_userdata_readback.verify` reopens all producer/effect/session records,
admission links and exact ordinary unmount/Close evidence. Actual .NET serialization
and Python dispatch/return tests cover this flow with synthetic native effects.
They do not prove native copying, protected placement or teardown. Ordinary-file
admission failure fixtures remain to be executed in the isolated runtime.

Read-only predecessor verification reopened 10 effect and 18 session records and
matched full target/journal/runtime hashes. No original backing was mounted or
changed. Current Windows backing-volume capacity fails the unchanged 180-GiB gate.
No copy, canonical reservation, positive attempt or native negative was dispatched;
both attempt budgets remain unused. Next: fresh successful capacity/pin/tool checks,
isolated staging and final backing readback, native negative, then the one positive
canonical attempt, independent handoff and exact teardown. No historical replay.

Scope remains nonempty complete selected ordinary trees, 32 MiB/1,024 entries,
existing bounded names and CreateNewSelectedTrees. Broader filenames, overwrite,
privileged metadata, profiles and Windows acquisition remain unsupported.
ProductionAuthentication is Unsupported and NativeSupported is 0. First boot,
agent, Enrollment and firmware remain outside scope. No canonical UserData success
or new retained UserData target is claimed.

## Canonical UserData execution - 2026-10-01

[Execution evidence](debian-userdata-canonical-execution-evidence.json) records the
new attempt `18e2b3e0-dcc9-45e0-bf8f-4a43f442e5dc`. Current capacity and protected
development-pin checks passed. The existing provisioner was connected to
`canonical_userdata_lab.derive` before backing allocation; independent target,
journal and runtime copies were verified. Ten real isolated ordinary-file admission
fixtures passed. Separate provisioning staged four selected synthetic files
(131,130 bytes, six entries), plus an unselected sentinel, on the copied FAT32
payload. Unrelated payload objects were independently unchanged. Staging shutdown,
full changed-target hashing and fresh second-launch correlation were recorded.

The separately bound negative dispatched once but stopped at `PredecessorReopen`.
It therefore did **not** qualify the intended wrong-operation rejection. Native
observer JSON containing `+` had been escaped as `\u002B` by durable .NET journal
serialization; the new successor reader incorrectly hashed that escaped form.
`DebianVerifiedInitramfs.ObservationHashMatches` now recognizes the original native
ASCII representation while retaining exact journal-chain hashes. Regression tests
reject a changed observation digest. Read-only reopening of the actual predecessor
then succeeded; no historical bytes or outcomes were changed.

The positive entrypoint dispatched once on the corrected predecessor-reader revision.
It returned `NotStarted` / `UserDataSessionUnavailable` / `JsonException` at
`StorageAndJournalValidation`, before canonical session creation. No target root
or payload was canonically mounted, no transfer/admission ran, and both new stores
remained empty. A separate read-only journal observation succeeded, but is not a
completed canonical placement or session result.

Afterward, the actual protected pin reproduced a strict-schema defect: UserData's
reader omitted its `ManifestSha256` and `ContentSha256` properties. The final source
uses the complete `UserDataExternalPin` schema and labels that boundary explicitly.
The focused regression both reproduces the old rejection and accepts the corrected
schema without allowing unknown fields. This post-attempt correction was rebuilt
and tested, **not dispatched natively**. The executed diagnostic does not identify
a narrower failure location than its recorded boundary; the pin mismatch is a
confirmed independently reproduced defect, not a recovered traceback.

Both authorized dispatch allocations are consumed (positive 1/1, negative 1/1).
No retry, reservation deletion or failed-root cleanup occurred. Provisioning's
journal mount was ordinarily unmounted with an exact independent mount delta;
canonical teardown is `NotStarted`, not qualified. Normal runtime shutdown was
observed separately. The powered-off failed derivative and all records remain
retained. Its complete target hash equals the post-staging hash; the protected
predecessor target and journal hashes were independently rechecked unchanged.

Next execution requires a separately authorized fresh attempt and native negative,
using the corrected pin reader after current prerequisites. The complete
`ForLabUserData` → `session_userdata.perform` → producer/observer → protected
admission → `canonical_userdata_readback.verify` → exact Close path remains
unqualified. SelectedDocumentTreesV1 limits and all product/first-boot gates remain
unchanged; there is no successful canonical UserData handoff.

## Corrected-input UserData canonical attempt — 2026-10-01

Current result: **not qualified**. The separately authorized attempt
`3d520fa6-876d-4f47-881c-beb089d90aea`, operation
`634294f1-7ad9-4fb1-99b2-faec481cad92`, consumed one positive dispatch and one
native negative. See [new execution evidence](debian-userdata-canonical-corrected-attempt-evidence.json).
The older failed attempt and its evidence retain their original meaning.

The existing 180-GiB host gate passed with 277,691,215,872 bytes available.
The current net8.0 harness built under SDK 10.0.401 without an Enterprise-2022
dependency or package/workload change. Shared read-only readiness reopened the
retained predecessor journal bytes and checked the complete protected external
pin, including manifest/content ancestry and expiry. Historical guest witnesses
are used only to reopen historical storage evidence; active continuation still
collects fresh inventory and guest witnesses. Protected parser copies are not
claims that an exported record's original parent path is protected.

After independent copy verification, bounded payload staging again delivered
four selected files, 131,130 bytes. A fresh launch repeated readiness against
its actual protected inputs. Changing only the operation reached exactly
`OperationBinding / UserDataOperationBindingRejected`, before canonical session
creation or producer effects. This qualifies the intended native negative.

The positive run verified canonical ownership/journal placement, acquired leases,
mounted root/payload and durably recorded **Baseline AppliedAndVerified**.
It then ended **OutcomeUnknown** at `TransferUserData`, before Transfer intent
or a producer reservation. No selected-file writer or admission ran.
Independent readback reopened ten session and two effect records and rejected
the incomplete handoff with `UserDataIncompleteChains`.

A separate read-only probe of the exact executed module reproduced its import
of nonexistent `block_session.CanonicalSession`; the actual class is
`MountSession`. The earlier execution's discarded stderr cannot be recovered.
Its recorded parent journal mount ID was 67, while independent supervisor
readback recorded the cloned mount as 209, exposing a second adjacent mismatch.

**Post-failure source, not native qualification:** the constructor now requires
the real exact `MountSession` type. Journal validation retains the original
device/inode and joins it to fresh independent path/mount readback in the active
namespace, requiring private persistent EXT4, exact mount root, noexec/nodev/nosuid,
and no aliases or child mounts. Import/attribute failures now reach the existing
Stopped record path. Twenty-three focused Python tests pass, including the real
constructor with actual .NET context JSON and synthetic filesystem boundaries.
The copier, receipt publisher and earlier scopes were not broadened.

Root/payload absence was observed in the runtime namespace after supervisor
disposal. **There is no canonical Unmount/Close receipt.** Provisioning's separate
journal mount was ordinarily unmounted with an exact observed delta; normal VM
shutdown was observed separately. The failed derivative and reservations remain
retained. Its target backing hash differs from staging; no post-failure whole-root
semantic readback was performed, so the session is not described as effect-free.

Both dispatch allocations are consumed. No retry or new authorization was issued.
A later authorized attempt must qualify the corrected context/journal connection
and still complete transfer, admission, independent whole-root delta, durable
handoff and exact canonical teardown. SelectedDocumentTreesV1 remains limited to
32 MiB / 1,024 selected entries and the existing filename/metadata policy.
No general Windows migration, first-boot, agent or production readiness follows.

## UserData context and journal qualification — 2026-10-01

Starting source checkpoint: `5e4ebe846b488d8609612d37665aaefd4d203c88`.
The existing real `MountSession` constructor and namespace-local journal join
were reused. The protected loader's existing verified-byte load sequence is now
directly testable. A new rejection test exposed an extra-device stacked mount
at `/lab-journal`; the validator now rejects that exact-path ambiguity as well
as its existing same-device aliases, child mounts, altered flags and identities.
See [this continuation's evidence](debian-userdata-context-qualification-evidence.json).

The actual .NET context, protected loader order and constructor-to-transfer setup
passed deterministic tests. Two small isolated native fixture passes were used.
The first failed before formatting because its fixture serial exceeded the
virtio transport's length; it remains retained. The corrected fresh fixture
passed the real private-namespace EXT4 journal join and native alias rejection,
with independently verified ordinary child and parent unmounts. Numeric mount
IDs are observations, not an equality/inequality identity rule. Neither fixture
grants canonical authority. All source and tool hashes were frozen before the
positive canonical dispatch; no generator, copier or admission policy changed.

Fresh capacity, protected predecessor/pin readiness, full copy verification,
staging, inventory, ownership and journal placement passed. The unchanged
wrong-operation guard reuses the earlier successful native negative; no new
negative ran. Fresh attempt `af4fafdb-99ee-48b5-81de-e5708b36173a`, operation
`f0843ad0-1bf3-4f27-b6ee-311f98fd1d5f`, session
`b9e67721-bd99-48f7-bbba-ec908a2731e7` consumed the one positive allocation.

Baseline passed, the actual `CanonicalContext` completed, Transfer intent was
persisted, and the real selected-document producer ran. Its separate terminal
chain records **AppliedAndVerified: four files, 131,130 bytes**, independently
checking contents, account ownership, source preservation and unrelated home
content. Exact raw producer records and its unadmitted receipt were independently
reopened after failure. This is not protected input-store admission or a complete
UserData session result.

The enclosing Transfer effect ended **OutcomeUnknown** before its successful
whole-root checkpoint. There is no Admission intent, final filesystem-delta
result, canonical Unmount or Close. Independent `canonical_userdata_readback.verify`
rejects the eleven session/four effect records with `UserDataIncompleteChains`.
The original child error was not retained. A separate read-only probe of the
actual persisted transfer plan confirms a deterministic mismatch:
`Documents/résumé.txt` is valid SelectedDocumentTreesV1 input, but
`configured_root.path_parts` rejects it with `UnsupportedPathEncoding` while
`userdata_successor.compose_predecessor` calls the inherited complete-tree walker.
This is a confirmed observer contract gap, not a recovered native traceback.
No path restriction was relaxed and no second operation was dispatched.

The partial target and journals remain powered off at
`/var/lib/igloo/initramfs-helper-labs/af4fafdb99ee48b581dee5708b36173a/`.
Protected raw exports are in `guest-evidence/`; `retention-result.json` binds the
powered-off backings. The successful predecessor target, journal and runtime
were fully rehashed unchanged. The preserved test object also matched. There
is **no post-failure whole-root preservation claim** for the partial target.
Runtime-namespace root/payload absence and ordinary provisioning-journal unmount
were observed separately; normal runtime power-down is not canonical teardown.
Earlier failed runs, reservations and their missing teardown evidence remain intact.

Next implementation boundary: compose precisely the validated selected-path
policy into successor filesystem observation while preserving the neutral
artifact validator, and retain bounded observer failure codes. Prove that join
with the actual .NET plan and fresh-process observer before any separately
authorized fresh canonical attempt. Do not replay this operation. Protected
admission, final whole-root readback, durable handoff and exact canonical teardown
still need native qualification. The 32-MiB/1,024-entry scope, production gates,
missing mandatory first-boot producers and all downstream restrictions are unchanged.

### Selected-document path policy and canonical lab completion — 2026-10-01

The successor whole-root observer now takes an exact, immutable set of selected
destinations from the validated `SelectedDocumentTreesV1` transfer bytes. This
allows the existing selection's `Documents/résumé.txt` spelling during complete
filesystem traversal and its unchanged-complement comparison. The neutral
artifact/import observer retains its strict default path policy. Missing,
additional, substituted and incorrectly typed selected paths still fail. The
four-file native fixture passed transfer, pre-admission whole-root observation,
protected admission, post-admission observation and the real result consumer;
its eleven mutation cases rejected unexpected content or metadata. This fixture
never supplied canonical ownership authority.

An intervening host shutdown interrupted provisioning attempt
`047f64d6-8321-43d0-8b6f-8daaf6146c83`. Its external one-use reservation was
independently reopened. Target and journal copy results exist, while its
runtime copy is incomplete. It has no launch, dispatch or canonical result
record. Its resources remain excluded from reuse. The separate interruption
assessment is retained at
`C:\igloo-lab-runtime\userdata-path-resumption-20261001\interruption-assessment.json`.

Fresh authorized attempt `a1b883f4-179f-4846-b16b-9a91fb4bcf9d`, operation
`6a079a17-5178-4feb-8826-c226ae73fa13`, and session
`1d5c9ba5-c903-4585-b0d9-9f84484ec05f` consumed the verified successful
initramfs predecessor. Its independent derivative and bounded FAT32 staging
passed fresh trust, capacity, hash, ownership and journal checks. Canonical
transfer copied four selected files, 131,130 bytes, into the authenticated
`iglootest` home, including the original accented filename. The actual
producer, fresh whole-root observers, protected admission and .NET consumer
verified the exact selected content, numeric ownership, configured state,
kernel/initramfs, packages and unchanged complement. The protected first-boot
input store contains only the validated bounded UserData receipt and evidence.

All eight effect records and eighteen session records independently reopened;
the final Close is `00000017-6F6663700278F96FC8FC9FF1324CDC8FF32B3D5B1C579D2C0A6F23D9F16DD567.json`.
Root and payload received ordinary exact canonical unmounts before Close. The
provisioning journal unmount and normal runtime power-off were observed
separately. The canonical dispatch completed on October 1; after the task
interruption, the protected raw chains, independent host handoff and retained
backings were reopened on October 3 without another transfer dispatch. The
target and journals remain powered off at
`/var/lib/igloo/initramfs-helper-labs/a1b883f4179f4846b16b9a91fb4bcf9d/`;
its `userdata-retention.json` binds the resulting backings and protected raw
records. See [the new execution evidence](debian-userdata-path-policy-canonical-evidence.json).

This completes **only** the lab-authored `SelectedDocumentTreesV1` scope:
32 MiB, 1,024 selected entries and the existing name/metadata policy. It is
not Windows/NTFS or cloud acquisition, general profile migration, agent
deployment, Enrollment or first-boot completion. `ProductionAuthentication`
remains Unsupported; `NativeSupported` remains 0. Earlier failed runs and
their unverified failure-path teardown remain unchanged.

### First-boot input composition after bounded UserData qualification — 2026-10-03

`DeploymentContent` is a required, generation-bound completion receipt in
`DebianFirstBootEvidence` and the restricted Python worker. The worker checks
its protected filename, exact byte hash and receipt envelope; it does not
verify deployment content or produce a missing receipt. The existing detailed
deployment contract requires an independently read-back ordered stage trace,
package and installed-file evidence, installed agent identity, bootloader
files, firmware finalization and preserved target evidence. No retained
DeploymentContent producer/result chain establishes those requirements. The
final `DebianInstallationReceipts.Assess` also requires first-boot success when
first-boot work is planned, so using that *final* assessment as a prerequisite
for the worker's DeploymentContent input would be circular. A later producer
must define and prove its pre-first-boot content boundary explicitly; the
current bounded UserData result cannot stand in for it.

`DebianFirstBootEvidence.InspectLabSelectedDocuments` now composes the exact
lab-only admitted UserData bytes with their declared initramfs predecessor,
generation, operation and account binding. It uses the existing admission
validator and returns only a read-only pending-input finding. The qualification
harness's stdin bridge accepts independently reopened protected input bytes,
then runs that same .NET composition. The read-only Python bridge first reopens
the original protected import, configuration, initramfs and UserData chains, checks the
predecessor result/Close join, and passes the actual retained receipt through
the restricted worker's validator. Its ordinary-file test also exercises the
worker's no-follow, owner and mode checks. The observed UserData receipt and
bundle hashes match the protected admission result. This path issues no
DeploymentContent receipt, creates no worker configuration or agent installation,
and confers no current target-write authority.

The next executable integration boundary is an independently observed agent
installation and pre-first-boot deployment-content stage/boot evidence producer,
with an exact protected DeploymentContent receipt. Enrollment remains a separate
required producer. Stable worker and unit files can be staged independently;
receipt-bound configuration, enablement and invocation require their inputs and
separate authorization. The current first-boot outcome is pending, not a
qualified service run. See
[the composition evidence](debian-firstboot-input-composition-evidence.json).

### Offline agent materialization fixture — 2026-10-03

The stable worker payload and exact restricted systemd unit can be materialized
before future receipt hashes exist. `DebianAgentProfiles.DeclareOfflineStage`
defines a distinct **fixture-only** `StagedInputsPending` state: the root-owned
0644 `/usr/lib/igloo/debian-agent.py` and
`/etc/systemd/system/igloo-deployment.service` are present and independently
read back, while `/etc/igloo/deployment.json` and the enable link are absent.
The stage binds generation, operation, target-root identifier, exact embedded
payload, unit bytes and source identities. It does not satisfy the existing
v1 `InstalledFirstBootPending` profile or authorize service execution.

The dependency order is stable worker/unit materialization and readback;
independently verified pre-first-boot deployment-content and Enrollment
producers; final receipt-bound configuration; exact enable-link publication;
then separately authorized service execution and observation. The eventual
DeploymentContent producer may use stable installed-file evidence without
requiring the hash of the future configuration that will consume its receipt.
This does not relax DeploymentContent's agent, boot, firmware or ordered-stage
requirements. Canonical stage authority, final configuration binding,
enablement and effective runtime isolation remain unimplemented and unqualified.

One small native fixture pass used the actual .NET generated stage, protected
create-new files, durable intent/result records and a separate-process
whole-tree observer. It passed exact worker/unit readback and unchanged
sentinels, with configuration and enablement still absent. Five focused Python
tests and the 460-test Migration suite passed. No canonical target was derived
or mounted; no worker or systemd manager ran. See
[the offline-stage evidence](debian-offline-agent-stage-evidence.json).

### Canonical offline-agent successor work — 2026-10-03

The current source adds a closed lab-only stage declaration and independent
configured-plus-initramfs-plus-UserData baseline/delta observer. The accepted
worker and unit bytes remain the intended two outputs. The observer retains
the selected-document path policy, including `Documents/résumé.txt`, and
requires configuration and enablement to remain absent. A read-only reopener
checks the successful UserData effect and session chains, protected admission,
package observation and exact Close. A source-level test exercises the actual
.NET stage packet and a complete small filesystem walk; it does not confer
canonical authority.

The host capacity gate currently rejects a new derivative: 163645120512 bytes
available versus the unchanged 193273528320-byte requirement at the latest
measurement. The successful UserData run's retained host directory also has a
UID-1000 ancestor, so the existing protected-path verifier rejects it. A
root-owned byte-identical diagnostic copy passed the read-only chain parser,
but that copy is not a replacement for protected original-path authority.
The retained external pin has a complete schema and its recorded time window
is open at inspection time, but full protected-path authentication was not
established. The earlier 4-of-36 tool-path probe was made in WSL: 31 declared
paths belong to the guest's `/opt/igloo-userdata-canonical`, so that result
does not show guest tools were removed or corrupted. A future native closure
must still be freshly staged and verified in its actual execution context.
The canonical MountSession dispatch, protected loader, durable stage result,
whole-root handoff and Close integration are still implementation work. No new
reservation, negative native case, positive canonical operation or fixture
acceptance invocation occurred in this continuation. The successful UserData
target remains untouched. See
[the canonical-stage work evidence](debian-agent-stage-canonical-evidence.json).

### Offline-agent canonical source integration and custody boundary — 2026-10-03

The current uncommitted source connects the lab-only offline stage to the
existing `DebianMountSessionAuthority` and `DebianNativeMountSession` action,
the protected Python loader and real `MountSession`, the qualified create-new
worker/unit publisher, a separate-process configured-plus-initramfs-plus-UserData
whole-root observer, durable effect/session checkpoints, and a strict
`DebianVerifiedAgentStage.Reopen` result/Unmount/Close reader. The publisher
adds only `/usr/lib/igloo`, the worker and the restricted unit. Configuration,
enablement, worker execution and `DeploymentContent` remain absent. The .NET
runtime closure now requires the agent modules and all their predecessor
dependencies when this stage is requested. Source-level loader/control-flow
tests use explicit synthetic native effects; the earlier accepted ordinary-file
publisher fixture remains separate evidence. No new canonical or native
acceptance invocation occurred.

The original host export at
`/var/lib/igloo/initramfs-helper-labs/a1b883f4179f4846b16b9a91fb4bcf9d/`
has a **UID/GID 1000:1000, mode 0755 run directory** (ext4 device:inode
`2096:105926`). Its root-owned `guest-evidence` child does not repair that
ancestor. The existing no-follow protected-path reader correctly rejects the
run directory. The storage provisioner deliberately changed it to the
unprivileged QEMU account so that the VM could open its backings. Earlier
successful readback ran in the guest against its journal and execution paths;
the newer reader instead treats the host export as a protected authority
store. Root-owned copied bytes passed chain parsing, but no independently
protected original terminal authority was established. The external
authorization/reservation binds the attempt, not the successful terminal
result. Historical paths under guest `/opt/igloo-userdata-canonical` cannot
be judged by probing that pathname in WSL.

The source-level stage is therefore **not native-qualified or currently
dispatchable**. A narrow, independently authenticated acquisition of the
original guest terminal records and their external result/Close anchors is
needed before the new operation can be reserved. No host directory permissions
were changed and the diagnostic copy is not authority. The current one-time
host capacity observation is also below the unchanged 180-GiB provisioning
threshold. See [the new source/provenance report](debian-agent-stage-source-integration-evidence.json).

### UserData protected completion handoff and offline-agent derivative — 2026-10-03

The future-successor path now has an explicit protected completion store,
separate from the UID-1000 QEMU run directory. `canonical_userdata_completion`
requires a separately protected terminal anchor and the external one-use
agent-stage authorization, then reopens the original raw UserData records,
independently validates the terminal effect and exact Unmount/Close chains,
and publishes exact bytes create-new with file/directory flush and readback.
The anchor binds result, Close, retention, generation, operation, session,
source run, target/journal identities and an exact raw-file inventory. The
publisher cannot mint its own anchor from a QEMU export. Failed publication
remains partial and cannot be replayed as a successful handoff.

`canonical_storage_lab.create` now invokes this read-only join before run
creation, reservation, copying or launch; its new agent-stage branch then uses
the existing independent-copy primitive and stages the verified raw completion
and authorization into the protected tools input. The .NET canonical entry
requires that completion envelope before reopening the UserData predecessor.
The older parser-only readiness command remains non-authorizing. Synthetic
tests exercised the actual retained record parser, protected handoff publisher,
Python provisioner preflight and current .NET completion reader; the test
anchor was deliberately generated from an untrusted export and has **fixture
scope only**. No new native stage or full-disk copy was run.

The historical `a1b883f4-179f-4846-b16b-9a91fb4bcf9d` export remains
blocked: its protected external authorization and reservation precede, and do
not independently anchor, the later UserData result and Close. The new code
cannot retrospectively establish that custody. A future run needs an
independently captured, protected terminal anchor from the trusted live guest
verifier and a protected acquisition of its raw records, followed by a new
external one-use agent-stage authorization. The trusted live guest-to-host
acquisition/call site has not itself been exercised or qualified; copying this
old export into a root-owned directory is still insufficient. The existing
capacity, development-pin, ownership, journal and native budgets remain gates
for a separately authorized run. See
[the handoff implementation record](debian-userdata-trusted-completion-handoff-evidence.json).

### Live UserData completion acquisition for a future run — 2026-10-03

The missing producer is now connected in source. A **new** UserData launch
records its exact QEMU arguments, tool-image hash, backing identities and
authorization digest in a root-protected witness outside the UID-1000 run
directory; a second protected witness binds the process ID and start time.
The host receiver and its exact Python dependency closure are copied create-new
into that protected authorization directory, hashed in a manifest bound by the
launch witness, and loaded with Python isolated mode after no-follow ownership
and byte checks. Mutable checkout modules are not read at live dispatch.
`canonical_userdata_acquisition.receive_live` checks those witnesses against
the current process arguments, open backing descriptors and UNIX console peer.
It sends only the fixed `--execute-and-capture` command and a fresh challenge
on that run's existing serial channel. The guest's real `execute` path must
finish with exit zero; `capture_completed` then independently reopens all
UserData effects and exact Unmount/Close records, checks the preserved volume,
retains the journals and ordinarily unmounts the provisioning journal. It
emits a bounded, fixed-kind, exact-byte frame. The host independently verifies
its terminal chains and stores raw records as `PendingBackingReadback` in a
root-protected completion directory. Guest-supplied paths never select host
destinations, and a saved console transcript is not accepted as acquisition.

After the guest has stopped, `finalize_retention` checks process absence,
open descriptors, backing identity, link count, timestamps and complete
backing hashes before writing the retention record and a **v2 live anchor as
an output**. The separate agent-stage authorization still nominates the later
operation and exact result/Close/backing digests. The existing protected
publisher copies the captured raw bytes and anchor create-new; Python and
.NET effect preflight require the v2 capture binding. The old v1 synthetic
anchor remains parser-fixture evidence and cannot authorize an agent-stage
derivative. Any interrupted pending capture or publication is incomplete and
cannot be replayed into success. Canonical Close, VM shutdown, capture and
retention remain separate observations.

Process-level socket and ordinary-file tests passed, including a fixture-only
terminal chain reused to exercise the finalizer, publisher and provisioner
join. No QEMU guest was launched for this acquisition task; native live
channel qualification remains pending. The earlier `a1b883f4` UserData export
cannot gain historical custody from this code. The nominated `c5dc04e9`
initramfs predecessor also has only authorization/reservation in its
root-protected directory, while its exported run directory is UID 1000.
The current successor trust rule therefore still needs an independently
captured initramfs result/Close and backing anchor, or a separately approved
trust decision, before a fresh UserData derivative can be authorized. See
[the acquisition evidence](debian-userdata-live-acquisition-evidence.json).

### Bounded live-channel fixture and Linux reader — 2026-10-03

A small fixture adapter now boots a verified retained lab runtime through a
fresh QCOW2 overlay, with one 64-MiB scratch disk and a read-only tools ISO.
It never attaches a migration target or journal. The ISO carries the current
guest framing module and explicitly labelled historical **codec** records;
transmitting those records cannot create historical custody. The protected
host closure, QEMU process/start-time witness, exact command line, open backing
descriptors, serial peer PID and per-run challenge are checked by the same
acquisition transport. Fixture output is restricted to a protected fixture
store and a v1 read-only parser path; the agent-stage effect path still
requires a canonical v2 live anchor.

The two authorized QEMU invocations were consumed without a complete accepted
capture. Pass 1 reached the guest console, then exposed a real Linux credential
join: QEMU creates the serial listener as root before `-runas` changes its
current UID to 1000. The receiver now accepts only that exact root-created,
unprivileged QEMU case after PID, start-time, command and FD validation. Pass 2
passed that join, but `read_controlled_channel` stopped on the shell's echo of
`IGLOO_CAPTURE_EXIT:$?` before the actual frame's terminal line. A later
diagnostic connection observed an END marker and zero-exit line, but missed
the frame beginning; it is **not** an accepted capture. The receiver now waits
for an exact zero-exit line, with focused regression coverage. This final
correction has not had a live acceptance pass. Both failed fixture resources
and their consumed budget records are retained. Neither is a canonical
UserData result or an exact canonical teardown. The independent Linux .NET
8.0.20 reader did execute against a protected ordinary-file fixture and
returned `Readiness=Passed`, `EffectAuthorization=false`; no live-captured
handoff reached it. [The new evidence](debian-userdata-live-qemu-evidence.json)
records the exact inputs, observed failures and current revision boundary.

The nominated lineage has a finite checkpoint to review, without granting
new authority to the unanchored successors:

| Nominated input | Historical execution | Present observation in this task | Current successor authority |
| --- | --- | --- | --- |
| `c5dc04e9` initramfs | Successful published image and Close in the retained summary | Root-protected authorization and reservation exist; full backing hashes were not repeated | Missing independently protected terminal result/Close and backing anchor |
| `60068939` configuration | Successful v5 configuration and Close | UID-1000 run directory observed; no fresh full backing hash | No new independently protected successor completion established here |
| `import-daa72a8f-20260929` checkpoint | Successful import with exact Close; configuration ancestry names it | Root-owned 0700 checkpoint, 0400 single-link target/journal, and result bytes hash to `518AF140…`; disk bytes were not rehashed | Protected checkpoint result is a finite candidate, pending fresh complete byte/chain verification and an explicit trust decision for a new lineage |
| Authenticated artifact/chunks | Original descriptor, manifest and five chunk hashes were verified in the import campaign | No fresh chunk-byte campaign here | Original artifact acceptance remains historical; fresh acquisition/authentication is required for a new lineage |

No nominated configured/initramfs successor was established as currently
admissible. A later authorized run must either start a fresh lineage from an
independently authenticated artifact or obtain a separately approved,
present-day checkpoint revalidation/trust decision. Revalidation can establish
the checkpoint's **current** bytes, ownership and chains; it cannot prove
unrecorded historical custody of the later UID-1000 exports. The 180-GiB
full-derivative gate, production rejection and all earlier outcomes remain
unchanged.

### Bounded live-channel framing continuation — 2026-10-03

The receiver now parses bounded lines across socket reads and requires the
nominated current unprivileged QEMU UID as well as the exact PID/start-time
join. Focused tests passed for echoed commands, fragmented CR/LF, trailing
output, nonzero exit, timeout and a still-root peer. The guest emitter now
starts the frame on a separate line and uses shorter DATA lines. The two
**new** authorized live passes were consumed, separate from the earlier 2/2
campaign. Pass 1 rejected a DATA line with `CaptureUnexpectedData` before
protected pending custody; its failed conjunct and original serial line were
not retained. Pass 2 ran the corrected guest and receiver but rejected a
protocol-prefixed line with `CaptureUnknownRecord`. No complete frame was
accepted, and the Linux .NET reader did not run on live-captured records.

At the last observation, pass-2 QEMU remained active despite separate-channel
ordinary poweroff requests; its writable overlay and scratch backings remain
open. The live budget is exhausted. A later run needs separate authorization,
bounded protocol-state diagnostics and a supported disposition for that
writer. After the failed pass, source-level tests added exact handling for a
wrapped literal shell echo, bounded unknown-record state, and the observed
post-capture QCOW2 length-growth case while preserving device/inode identity.
These later changes have **not** run in a guest and are not a successful live
capture. The protected import
checkpoint remains the previously identified finite candidate for a
separately approved real lineage. [Continuation evidence](debian-userdata-live-qemu-framing-evidence.json)
keeps these failures distinct from canonical UserData completion.

### Live fixture writer disposition and raw receive diagnostics — 2026-10-03

The outstanding `21f57f2a` QEMU writer was reidentified from its protected
launch/process records, current PID/start time, exact arguments, unprivileged
identity and open scratch-only descriptors. A bounded ordinary `poweroff`
request did not establish guest shutdown. The exact process was then sent
SIGTERM through a revalidated pidfd. Its exit, writer exclusion, final fixture
backing identities and unchanged read-only runtime source were observed. This
is **host-controlled termination**, not guest shutdown or canonical Close.

The sole receiver now retains bounded, create-new protected incoming bytes
before decoding, a bounded rejected-line record, parser offsets/state,
chunk timing, terminal markers and executed identities. These records are
untrusted diagnostics and cannot authorize completion. Process/socket and
pseudo-terminal tests, including an exact replay of the first new live
failure's raw bytes, passed. The replay exposed the cost of fsync on every
receive chunk; the protected diagnostic writer now syncs at bounded byte
intervals and at completion.

Two separately authorized **new** live QEMU passes were consumed. Pass 1
timed out after BEGIN and 8,459 complete DATA lines, without END or terminal
exit. After the diagnosed diagnostic-write correction and focused tests,
pass 2 also timed out at its 120-second fixture deadline. Its exact protected
prefix parsed without a malformed record: 6,176,768 of the declared
11,254,128 Base64 body bytes had arrived, in 5,707,411 mostly tiny socket
receives; END and terminal exit had not arrived. This establishes a bounded
transport/deadline failure, not a newly proven cause for the older
`CaptureUnexpectedData` or `CaptureUnknownRecord` failures. Both new guests
were host-terminated after bounded normal shutdown requests failed; both
writers are excluded and the runtime source is unchanged.

Neither pass produced a complete capture, protected fixture publication or
Linux .NET readback on newly captured records. The old ordinary-file reader
test remains distinct. The live budget is exhausted; another invocation
requires separate authorization and a measured correction for the current
one-byte-heavy serial transfer/deadline, with focused regression coverage.
No canonical UserData or agent-stage allocation was consumed. The earlier
protected import-checkpoint starting-point decision is unchanged. [The new
diagnostic evidence](debian-userdata-live-qemu-diagnostics-evidence.json)
references protected raw observations without publishing console bytes.

### Full-size live fixture capture and Linux reader — 2026-10-03

The protected 120-second trace showed a steadily progressing 11,254,128-byte
Base64 body delivered mostly in tiny serial reads. The receiver now avoids
repeated partial-line scans/copies, batches its bounded protected diagnostic
stream, and samples chunk timing without making millions of metadata objects.
Its diagnostic fsync policy is 1 MiB or one second, plus completion. The
fixture-only absolute capture ceiling is 360 seconds, derived from the
observed approximately 219-second body extrapolation with explicit margin.
Separate boot, BEGIN, valid-progress and terminal-status bounds remain in
force; the canonical execution deadline is unchanged.

The complete 8,440,594-byte fixture packet passed the actual emitter,
receiver and diagnostic path under one-byte, coalesced and simulated-slow
delivery. The one-byte local regression processed 11,397,319 wire bytes in
about 11 seconds with 180 diagnostic writes; the 222-second simulated stream
also completed. Focused tests passed, including distinct deadline failures,
real peer/PTY tests, and normal versus host-controlled lifecycle fixtures.

Two new small QEMU passes were consumed. Pass 1 accepted a complete frame
in 249 seconds and independently reopened the fixture terminal chain, but
its adapter closed the serial connection after requesting `poweroff` and
treated the unverified 30-second shutdown as a terminal failure. Its exact
failure, accepted capture and writer exclusion are retained. The corrected
adapter keeps the sole connection open for bounded shutdown observation. It
then uses a PID/start/FD-revalidated pidfd SIGTERM request when normal exit
is unverified, preserving that distinction before final backing readback.

Pass 2 accepted the complete real-guest frame in 247 seconds, verified all
11,254,128 body bytes, END and zero exit, reopened the eight effect and
eighteen session codec records, published the protected fixture store and
independently reopened its exact raw bytes and anchor. The authenticated
Linux .NET 8.0.20 reader ran on **those newly captured records** and returned
`Readiness=Passed`, `EffectAuthorization=false`. The effectful agent-stage
entrypoint rejected fixture provenance at `ProtectedPredecessor` before any
effect. The read-only runtime source remained byte-identical; both new QEMU
writers exited and their scratch/overlay identities were retained.

Normal guest shutdown was **not** verified in either pass. The second pass
retained bounded console diagnostics, requested targeted SIGTERM after the
normal wait, then established process exit and writer exclusion. Exit code
zero does not retroactively prove that `poweroff` completed before the signal.
This is live fixture capture-to-reader qualification, not canonical UserData
completion, historical custody, clean guest shutdown or canonical Close.
The prior protected import-checkpoint starting-point decision is unchanged.

### Nominated import checkpoint present-day read-only assessment — 2026-10-04

The only nominated checkpoint, `import-daa72a8f-20260929`, remains a **candidate**, not an accepted new lab input. Its root-protected original-path records and single-link, read-only 40-GiB target and 8-GiB journal were inspected without a current writer or attachment. Complete current backing hashes matched the checkpoint bindings. The protected development pin was still valid, and the retained descriptor, transport, manifest and all five content chunks passed the existing authenticated per-file source verifier. The historical guest export's 1,823 import and 18 session records passed the independent chain reader, including its import terminal and exact Close references; that export parse alone is not direct present-day journal-filesystem verification or a new custody anchor.

One private read-only `noload` filesystem inspection session ended before its observers: the inspection script required `findmnt` to display both `ro` and `noload` for the journal mount, and its combined assertion failed. The exact failed predicate was not retained. The journal mount was ordinarily unmounted; the target was never mounted. The scratch directory was removed, and no nominated writer or attachment remained. The complete current root, package, neutral-state and direct journal observations are therefore **incomplete**. A create-new root-protected **pending** assessment was fsynced and independently reopened at `/var/lib/igloo/import-checkpoint-assessment-daa72a8f-20261004/assessment.json`; it is not an acceptance or effect authorization. No protected successful acceptance record was issued. [The new public summary](debian-import-checkpoint-present-day-evidence.json) records measured hashes, this precise stop and the unchanged historical-custody limit.

At this earlier assessment, the finite later path first needed an explicitly accepted current import input, followed by a freshly authorized configuration continuation through `ContinueLabCheckpoint` / `--configure-derived-lab`, fresh initramfs and UserData successors, and canonical agent staging. That pending assessment did not authorize those phases. The current protected live completion publisher and agent-stage preflight are specifically `TrustedLiveUserDataTerminalV2`; no corresponding configuration or initramfs trusted host-acquisition adapter was identified in the scoped implementation review. Their existing result-chain readers do not by themselves supply that successor-input custody. The single Windows capacity observation for a later full derivative was 137,106,751,488 bytes available against the unchanged 193,273,528,320-byte gate, a 56,166,776,832-byte shortfall.
[Execution evidence](debian-userdata-live-qemu-throughput-evidence.json)
references protected diagnostics and records without adding raw console bytes
to Git.

### Nominated import checkpoint accepted as a present-day lab input — 2026-10-04

The newly authorized **read-only** inspection completed the previously missing boundary for the exact `import-daa72a8f-20260929` checkpoint. A small disposable EXT4 mount first showed that the kernel records requested `noload` as `norecovery` in mount super-options. The corrected observer requires a private namespace, exact loop source/offset/size and device, read-only loop and EXT4 mount, `norecovery`, exact mount root, and no child, stacked or alias mount. Focused rejection tests and the real small fixture passed before the one checkpoint session.

The checkpoint files retained their previously fully hashed identities and timestamps, so the 40-GiB target and 8-GiB journal were not rehashed again. Current protected checkpoint bytes, externally pinned development artifact inputs, pin validity and activity checks passed. The present-day journal filesystem was attached read-only with journal replay disabled; its **actual mounted records**, not the historical host export, passed the 1,823-record import and 18-record session chain reader, terminal import result and exact historical Unmount/Close checks. Only then was the root attached read-only. The existing complete observer matched all 144,911 manifest entries, 1,594 configured packages and neutral imported state. The target and journal inspection mounts were ordinarily unmounted in reverse order; fresh host-context checks found no writer or loop attachment and the checkpoint identities unchanged. Inspection teardown is a new observation, not a new canonical Close.

The protected observation store at `/var/lib/igloo/import-checkpoint-inspection-20261004-v2/` was independently reopened. A separate create-new, root-protected `PresentDayReadOnlyImportCheckpointLabInput` record at `/var/lib/igloo/import-checkpoint-acceptance-daa72a8f-20261004-v1/acceptance.json` was independently reopened with SHA-256 `2516C1D52CE0882FF9F6F11825D8B186303A01AC8268610D81ED7A218D81C369`. It accepts **only this verified current checkpoint state** as a development-lab starting input under the user's conditional approval. It neither proves historical custody of later UID-1000 exports nor grants a one-use configuration authorization or permission to write the checkpoint.

A later separately authorized lineage can use `ContinueLabCheckpoint` through `--configure-derived-lab` with the protected predecessor, continuation, runtime, journals, current external pin and a new external one-use authorization. The existing configuration and initramfs readers verify result chains, but stage-specific trusted host completion-capture/admission adapters are still needed before each can authorize its successor; `TrustedLiveUserDataTerminalV2` is UserData-specific. No phase was executed here. Windows had 146,186,768,384 bytes available against the unchanged 193,273,528,320-byte full-derivative gate, a 47,086,759,936-byte shortfall. [The new acceptance evidence](debian-import-checkpoint-present-day-acceptance-evidence.json) gives the protected raw references and exact scope.

### Configuration/initramfs closed host lifecycle source join — 2026-10-12

`canonical_stage_lifecycle.py` now provides one internal lab-only `run`/`publish` caller for either stage. It checks the nominated input, one-use stage authorization, current pin and frozen root-custodied source/tool set. Immediately before the existing derivative creator starts QEMU, a closed hook joins the frozen bytes to the actual staged guest, host and harness files, their inventories and protected launch witness. It owns the launched process immediately, independently checks the controlled launch and backings, performs the fixed hash-bound isolated guest preparation, then calls the existing `complete_live` receiver/disposition/retention path once. Durable controller success, independent stage readback, a bounded Linux .NET successor read, separate successor authorization, protected publication and independent reopen of the existing published result follow in that order. A lost response after durable completion takes the read-only reopen path; no stage replay is offered. The stage publisher now requires the caller's independently reopened reader result for caller-owned runs, so direct `publish` cannot bypass a failed reader. Configuration and initramfs retain distinct predecessor and successor contracts.

Source and process fixtures exercised both stage choices, fixed-command framing, controller-success/publication validation, missing publication authority, cancellation, failed joins and lost responses. The real protected Linux .NET 8.0.20 runtime executed against new completed synthetic process stores and correctly rejected them because the fixtures contain only an import-ancestry proof marker, not the complete import `storage.json` chain; successful Linux predecessor decoding in this new caller is **not** qualified by those fixtures. The effectful QEMU, block acquisition and Linux reader-success boundaries were doubled or unavailable in the positive process fixtures. No QEMU run, real one-use reservation, canonical completion, successor admission or deployment occurred in this task. A future native run still requires its own current pin, full derivative capacity, protected tool-stage closure and separately issued stage/successor authorities; neither the present-day import acceptance nor this source join supplies those permissions.

The current source-only protected stage passed the caller's actual 122-file closure check. A clean, isolated .NET harness publish included the Core and Debian provider outputs and excluded generated agent `__pycache__` files; its 59 staged files matched the publication bytes. These are source and package observations, not proof that the staged modules ran inside a Debian guest. The earlier qualified live transport and successful historical checkpoint scopes remain unchanged.

### Complete synthetic predecessor Linux reader join — 2026-10-12

The missing positive **read-only fixture** check now uses a .NET exporter that builds complete synthetic import, configuration and initramfs chains with the existing typed storage and stage contracts. Both exported trees independently reopen through the actual Linux .NET 8.0.20 configuration and initramfs readers with `EffectAuthorization=false`. The host caller process tests then capture the exact exported ancestry, pass their stage-specific readback and controller-success barriers, invoke those same Linux readers, publish through the existing protected fixture publisher and independently reopen the results. The positive tests retain real root-owned path checks for their small fixture stores; QEMU launch, disk effects, source acceptance and the initramfs predecessor authorization remain explicit process-fixture substitutes.

The join exposed an initramfs plan-fingerprint mismatch: the predecessor shaper had reserialized a `JsonElement` from the captured preflight, changing its raw lexical form. It now publishes the exact verified `Plan` member bytes, including nested string escapes and property order. Focused tests reject missing or changed import/Close, stage result, observation, generation, target, source manifest and controller evidence; malformed fixture input cannot reach publication. The fixture completion and one-use authorization directories are outside the effectful default stores, and the real effect preflight rejects their paths. This establishes the two reader/caller **structural** joins only. It creates no current native checkpoint, operation authority, canonical stage result or production readiness, and does not replace a future isolated guest run with real backing and lifecycle observations.
