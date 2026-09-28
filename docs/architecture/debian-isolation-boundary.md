# Debian execution boundary — Community #241

Status: **DEBIAN ISOLATION FOUNDATION BLOCKED** (2026-09-28).
This continues the existing [Debian protocol](debian-target-root-deployment.md).
It does not enable preparation, deployment, registration or firmware finalization.
There is still no qualified production `IDebianIsolatedStageHost` composition.

The subsequent [canonical session continuation](debian-session-boundary.md) adds
shared block-lease declarations, a persistent native mount-session protocol and
durable resolver/journal callbacks. It also strengthens connected-FD validation.
That checkpoint supersedes the missing-IPC statements below, but does not qualify
real GPT mounts, a complete stage host or the debootstrap privilege transition.

## Implemented boundary and its limits

The new native package broker actually executes a restricted child. It is not a
shell/chroot wrapper or a set of requested flags reported as successful isolation.
`package_broker.py` uses an authenticated Debian bubblewrap binary, the small
`command_gate.c` supervisor/gate, a sealed libseccomp program, and a fresh external
`isolation_observer.py` process. The child stops before executing the target tool.
Only successful kernel readback, a durable-intent callback, and a second unchanged
readback permit release. Command exit is evidence, not stage success.

`mount_supervisor.py` implements FD-anchored native filesystem mount mechanics,
independent readback, and exact unmount. Its canonical resolver callback and
persistent deployment session are **not yet composed with the shared .NET
inventory/resolver**. The package broker consumes mounted directory leases; these
are not a new ownership model and do not certify GPT/UUID identities themselves.
Never pass an arbitrary existing directory as if it were an owned root filesystem.

Real native qualification used ordinary disposable directories and private tmpfs,
not GPT partitions, a FAT32 ESP, loop devices, debootstrap or apt installation.
No end-to-end stage in the 43-stage production protocol is promoted by these tests.

## Threat model

The target tool and all maintainer scripts are less trusted than the supervisor.
They may execute arbitrary code within the intended owned Linux root. The kernel,
trusted runtime/broker/observer, canonical preparation receipts and exclusive host
supervisor are trusted. A compromised host root, kernel exploit, malicious kernel
module or uncooperative privileged process replacing mounts is outside this model.
The policy does not certify arbitrary package compatibility or prevent every
resource-exhaustion attack. Runtime library closure, process/memory limits and VM
evidence remain requirements; a binary hash alone does not qualify that runtime.

| Attempt by target code | Enforcement and independent evidence |
| --- | --- |
| Open Windows/data/recovery/unrelated raw disks | No block nodes or raw device descriptors in child or namespace init; reject even a synthetic block node before release; root/payload/ESP mounts nodev; mknod APIs denied and MKNOD removed |
| Manufacture access through another pathname | No inherited setup FDs; new PID/proc view; init has the same root and no directory/raw-device FDs; open-by-handle, pidfd-getfd, ptrace and process-vm APIs denied |
| Mount, remount, bind, pivot or enter namespaces | SYS_ADMIN/SYS_CHROOT absent, both mount API generations and namespace entry/creation denied |
| Write efivarfs or another firmware interface | Empty read-only /sys, no efivarfs, no /dev/mem, EFI/capsule/raw-I/O devices; read-only /proc; RAWIO/ADMIN absent; network/netlink and I/O privilege syscalls denied |
| Reboot, kexec, load modules, alter clock | SYS_BOOT/SYS_MODULE/SYS_TIME absent; relevant syscalls denied |
| Reach host systemd/D-Bus/Docker/NetworkManager/SSH sockets | Private /run, no inherited sockets, whole visible owned tree socket scan, separate network/abstract-UNIX namespace |
| Reach host network or live APT repositories | Private network namespace; only loopback appears; non-UNIX socket creation denied; explicit environment without proxies; file source still readable |
| Replace root, ESP, source or mount propagation | Pinned directory inode/device/mount ID; independent namespace and mount readback; second read after intent; read-only child ESP/source; no child mount capability |
| Escape through symlinks or a changed executable | Existing openat2 no-symlink tool open; sealed hash-verified tool overlay; trusted static gate; critical resource inode comparison before tree inspection |
| Regain privilege through exec, set-ID files or file capabilities | NoNewPrivs, nosuid mounts, exact effective/permitted/inheritable/ambient/bounding masks, no SETPCAP after gate, no new namespaces |

This is layered device denial, **not a claimed cgroup device controller**. Absence
alone is insufficient; nodev, no raw FDs, denied creation/mount/namespace operations
and capability removal are all required. No real Windows raw device was opened
for a test. The native fixtures use absent paths and an unregistered synthetic
block-node identity to test refusal without host-storage access.

## Process and namespace model

1. A trusted root supervisor process unshares its mount namespace and makes `/`
   recursively private **before** any source binds. This changes only that process's
   namespace, not the host mount propagation.
2. Pinned bubblewrap creates another mount view plus PID, network, IPC and UTS
   namespaces, with a fresh filesystem root assembled from exactly three leases.
   No optional `--try` fallback is accepted. Initial-host user namespace is retained
   so Debian can create real target UIDs; a single-UID user mapping is not claimed
   compatible with package/account configuration. Further user namespace creation
   is denied. The child gets a new session and no controlling host terminal.
3. Bubblewrap's minimal PID 1 reaps descendants. The trusted static gate closes
   all FDs above 2, removes every unrequested capability from the bounding set,
   removes its own temporary SETPCAP capability, and stops with SIGSTOP. The
   syscall filter already applies at this point.
4. A fresh host observer reads `/proc/<pid>` and the namespace-init process. It
   checks actual namespaces, identities, mount modes/propagation, devices, sockets,
   descriptors, capabilities, NoNewPrivs and seccomp mode. It executes no target code.
5. The broker requires durable intent, repeats readback, then releases the exact
   pidfd. A timeout/death remains a failed/unknown command; no automatic retry or
   stage promotion follows. Output is discarded, including for credential commands.

Per-stage capabilities (Linux numbers in parentheses):

| Profile | Retained capabilities | Purpose |
| --- | --- | --- |
| Package | CHOWN (0), DAC_OVERRIDE (1), FOWNER (3), FSETID (4), SETGID (6), SETUID (7), SETFCAP (31) | Package ownership/modes, service accounts, target file capabilities; actual package compatibility unqualified |
| Configuration | CHOWN, DAC_OVERRIDE, FOWNER, FSETID, SETGID, SETUID | Account/configuration operations on the owned root |
| Observer fixture | none | Independent target read-only command profile |
| Bootstrap / signed loader / firmware | **no supported profile** | Cannot inherit package policy or request broader privilege |

SETPCAP is supplied only to the trusted gate to remove bounds, then removed before
readback or target execution. SYS_ADMIN, RAWIO, BOOT, MODULE, MKNOD and SYS_CHROOT
are never retained by target commands. Actual masks are checked, not inferred
from UID 0. See [capabilities(7)](https://man7.org/linux/man-pages/man7/capabilities.7.html).

The amd64 filter denies old/new mount APIs, chroot/pivot, swap, reboot/kexec,
module operations, I/O privilege, mknod, namespace operations, handle-based opens,
cross-process memory/FD access, BPF/perf, io_uring, keyring controls and clock/host
configuration. clone namespace flags are denied; clone3 returns ENOSYS for libc's
ordinary clone fallback. Non-UNIX socket creation and TIOCSTI/TIOCLINUX are denied.
Unknown ABI/library support fails before launch. The sealed BPF hash is retained
alongside independent seccomp-state evidence; runtime tests exercise forbidden
calls. [seccomp(2)](https://man7.org/linux/man-pages/man2/seccomp.2.html) does not
make a syscall denylist alone into a storage sandbox.

## Mount and device policy

Package view: owned root rw (observer profile ro), Linux ESP **ro**, payload **ro**.
All three are nodev/nosuid. `/dev` is a new tmpfs with only null, zero, full,
random, urandom, tty, private devpts/ptmx and standard FD links. There is no console
bound from a host TTY, full host `/dev`, block device, loop device or device mapper.
`/sys` is empty read-only tmpfs; `/proc` belongs to the new PID namespace and is
read-only; `/run` and `/tmp` are private tmpfs. The source alias is explicitly
read-only. Any extra mount, socket, device or propagation relationship rejects
release. An initramfs tool needing a richer sysfs/device view remains unqualified
until that exact need is bounded; no automatic widening exists.

The supervisor's `mount_block` accepts only root/LinuxEsp/payload roles, exact
generation-derived destinations and block FDs with expected major/minor/access.
It requires the fresh shared canonical witness before and after mounting; the
witness must cover disk/GPT/UUID/type/geometry, not just the transient number.
Already-mounted sources and aliases are rejected. Linux ESP/payload are mounted
read-only. fsopen/fsconfig/fsmount create a detached filesystem; move_mount attaches
it to the pinned destination FD. Intent precedes attachment and fresh-process
mountinfo/stat readback follows. New mount effects must match the expected delta.
The native canonical witness producer/session IPC is still missing, so this route
has **not** been run against a real owned block device.

Unmount order is helpers, ESP, payload, root. Before each operation, the exact
mount ID, path, inode/device, namespace and lack of dependent mounts are reread.
The held mount FD closes before unmount (otherwise it itself keeps the mount busy).
A pinned parent plus final-component `UMOUNT_NOFOLLOW` names that exact mount;
there is no force/lazy/recursive unmount. Disappearance and absence of other mount
changes are independently checked. Busy, observer failure or persistence failure
poisons the session. Intent is retained; no forward operation or blind retry occurs.
See [fsopen(2)](https://man7.org/linux/man-pages/man2/fsopen.2.html) and
[mount(2)](https://man7.org/linux/man-pages/man2/mount.2.html).

## Findings that prevented unsafe qualification

- Actual bubblewrap 0.12.0 initially retained a broad bounding set and imported
  `master:` propagation links on pseudo devices. Strict readback rejected both.
  Outer recursive-private setup and explicit gate bounding-set removal fixed these;
  the checks were not relaxed.
- A rejected experiment passed detached open_tree directory FDs directly as
  bubblewrap sources. The resulting source expansion selected the WSL filesystem
  root, rather than the intended fixture subtree. **No target command was released.**
  Setup nevertheless created an empty `/boot/efi` directory and zero-length
  `/usr/bin/probe` in WSL. Both were inspected (same WSL root device, no mount at
  either path, exact new timestamps/type/size) and removed individually; independent
  absence was verified. No Windows ESP, physical partition or firmware changed.
  That implementation was removed. Original connected directory leases now require
  exact path/inode/mount-ID agreement, and any imported child mount is rejected.
  Early resource identity checks precede observer tree walking. A production
  supervisor must provide a separately verified root-only view; the current shared
  assembled root/helper mount context is not silently accepted as equivalent.
- Directory fixtures are not canonical partition evidence. Block/FAT32/GPT mount
  qualification, namespace-session ownership, runtime ABI/library closure and full
  stage readback remain open. These are isolation blockers, not merely later VM
  bootability tests.

## Debootstrap 1.0.141 remains explicitly blocked

The previously authenticated helper tree was reread. Both normal and second-stage
entry run `check_sane_mount`: mknod/write and, on failure, a bind-mount attempt.
`first_stage_install` ends with static device setup. `second_stage_install` invokes
setup_proc before dpkg and can tolerate failed mounts/unmounts; its exit hook even
uses lazy proc unmount. A successful exit alone therefore cannot certify setup.

The documented `--foreign` split separates first-stage extraction from package
configuration, but does not remove second-stage device/mount checks. No supported,
qualified handoff was established that runs those setup operations with privilege
while ensuring all later maintainer scripts inherit only the package profile.
No helper patch, fabricated CONTAINER value, fakechroot, full `/dev`, broad SYS_ADMIN
grant or ignored helper failure was introduced. Bootstrap launch declarations
remain Unsupported. **Real debootstrap did not run under this broker.**
The next implementation needs a supported supervised bootstrap transition (or a
separately approved authenticated base-image strategy), not permission widening.
[Debootstrap's public interface](https://manpages.debian.org/trixie/debootstrap/debootstrap.8.en.html)
and the retained authenticated source evidence define that open boundary.

## Typed API, durability and integration

`DebianIsolatedLaunches` derives stage/profile, argv/input, generation/plan hash,
runtime/tool identity, mounts, exact capability mask, timeout and bounded environment
from existing Debian plans/instructions. Substituted arguments, environment, target,
generation or profile fail; unsupported/denied/unavailable tool observations retain
their states. No shell-string entry point was added. The native input channel
accepts only sealed descriptors, never credential argv. Connecting the existing
single-use `DebianSealedCredentialInput` producer/consumer to that channel remains
part of the missing production session adapter.

Native package execution requires a durable-intent callback before release. Native
mount execution requires intent/result checkpoints and poisons itself on failure.
These callbacks must be composed with `DebianLinuxDeploymentJournal` and independent
reopen; fixture callbacks are not production durability. The 43-stage runner still
requires its separate semantic observer. No exit-zero conversion to stage success,
RecoverySnapshot Exact, recovery readiness, authorization or rollback was added.

## Qualification evidence and remaining work

Actual signed Trixie metadata authenticated bubblewrap **0.12.0-1~deb13u1**:

- Debian archive SHA-256: `70ACA4FA8DAEACB677EC00E8063EB586F08AE3D94B1F11E684370B5524C43431`.
- Extracted executable SHA-256: `573236E5328AC2EBB08F59AE3A9805B4F8D12BDEF14BE8AF4450D5463294985F`.
- Candidate runtime: WSL Linux `6.6.87.2-microsoft-standard-WSL2`, Ubuntu 24.04,
  libseccomp 2.5.5; static gates/probes compiled with warnings as errors. This is
  **not** the qualified Trixie deployment runtime. No host package was installed.

The explicit native rehearsal tests new namespaces, real syscall denials, safe
directory writes/readback, read-only ESP/source views, sealed tool execution and
private tmpfs mount/unmount. It checks that the parent mount table is unchanged.
Fixtures also inject intent/result fsync errors, unavailable observers, attachment
followed by error, busy/unavailable unmount, path replacement, child death and timeout.
Temporary fixture trees and the two experimental WSL placeholders were cleaned up.

Remaining isolation work: production canonical block-lease acquisition and session
composition; exact root-only view and lifecycle integration with shared mounts;
qualified bootstrap/setup split; full pinned runtime; protected-input connection;
complete semantic observers and journal integration; resource limits and disposable
VM tests against actual prepared GPT root/FAT32 ESP/source. The full GNOME `wsdd`
closure issue and first-boot producers remain separate unchanged blockers.

No full target-root/loopback/debootstrap/apt rehearsal, bootloader or firmware
finalization occurred. No production stage is newly certified. The result remains
**BLOCKED**, not a claim that only signed-loader/NVRAM work remains.

## Validation commands and results

All .NET targeted projects used `--no-restore -m:1 -warnaserror`:

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Core | 330 | 0 | 0 |
| Preflight | 343 | 0 | 0 |
| Community.App | 58 | 0 | 0 |
| Migration | 275 | 0 | 0 |
| Debian filter (Migration subset) | 244 | 0 | 0 |
| Linux `test_*.py` discovery | 109 | 0 | 0 |
| Explicit native `broker_rehearsal.py` | 36 | 0 | 0 |

`dotnet build -warnaserror`: **0 warnings / 0 errors**.
`dotnet test Igloo.sln --no-build --no-restore -m:1`: **1,195 / 0 / 0**.
`git diff --check`: exit **0**; existing LF/CRLF notices remain separate.
Added 18 .NET, 43 Linux policy/primitive tests and 36 explicit native tests.
The native suite is intentionally separate from normal unit discovery: it requires
the explicitly authenticated bwrap, a root setup supervisor, gcc/static libc, and
the supported kernel. Missing prerequisites fail the explicit run, not silently skip.

Native reproduction after independently authenticating/acquiring the runtime:

```text
IGLOO_BWRAP_PATH=<authenticated extracted binary>
IGLOO_BWRAP_SHA256=573236E5328AC2EBB08F59AE3A9805B4F8D12BDEF14BE8AF4450D5463294985F
python3 -B tests/installer/broker_rehearsal.py
```

Run only in the controlled Linux fixture environment, with no real device supplied.
The ordinary-directory and tmpfs suite is not a loopback installation rehearsal or
disposable VMware acceptance. No complete package bundle or production source
observer was qualified by this continuation.
