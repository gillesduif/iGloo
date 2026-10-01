# Core configuration compatibility review — 2026-09-29

**Native outcome: incomplete.** The single authorized attempt passed Baseline,
Files and Debconf, then stopped at Exim. The review missed the helper's explicit
`root:Debian-exim` output ownership; the executed observer incorrectly required
root:root. The correction below is source/fixture-tested only. There was no retry.

This review precedes the single newly authorized checkpoint-derived experiment.
It is diagnostic evidence, not an ownership capability or a configuration result.
The two earlier consumed configuration operations remain OutcomeUnknown, without
canonical teardown. Neither their targets nor their reservations are reused.

The [machine-readable report](debian-core-configuration-compatibility.json) records
the exact source manifest binding, 62 path checks, package versions, retained input
hashes, 74 executable/interpreter/library objects and the declared helper footprints.
All selected regular-file bytes were read from the retained chunks and compared
with the authenticated manifest. No checkpoint filesystem was mounted for review.
Private account database contents were not exported.

`session_configuration.compatibility` supplies both this review and native preflight.
Native preflight additionally uses the existing FD-relative `TargetFiles.observe`
against the newly mounted target. It compares complete before-state, including file
hash, owner, mode and link target, before the configuration effect intent. A failed
parent-chain check stops inspection through that parent. Independent path failures
are collected rather than treating the first one as a complete review.

| Files destination | Authenticated before-state | Declared operation |
| --- | --- | --- |
| `/etc/hostname` | Absent | Create regular 0644, target hostname |
| `/etc/hosts` | Regular 0644 | Replace with deterministic loopback/target names |
| `/etc/mailname` | Absent | Create regular 0644, target mailname |
| `/etc/exim4/update-exim4.conf.conf` | Regular 0644 | Replace only the two reviewed identity values |
| `/etc/locale.gen` | Regular 0644 | Select `en_US.UTF-8 UTF-8` |
| `/etc/locale.conf` | Regular 0644, 35 bytes | Write `LANG=en_US.UTF-8` directly |
| `/etc/default/locale` | Link to `../locale.conf` | Preserve; not a write destination |
| `/etc/timezone` | Absent | Create regular 0644, `Etc/UTC` |
| `/etc/default/keyboard` | Regular 0644 | Replace with declared US layout |
| `/etc/network/interfaces` | Regular 0644 | Replace with loopback-only policy |
| `/etc/apt/sources.list.d/debian.sources` | Absent | Create final Debian source contract |
| `/etc/fstab` | Empty regular 0644 | Write shared generator output using fresh root/ESP UUIDs |
| `/etc/localtime` | Link to `/usr/share/zoneinfo/Etc/UTC` | Explicit link operation with the same target |
| `/etc/resolv.conf` | Link to `/run/NetworkManager/resolv.conf` | Explicit link operation with the same target |

All listed parents are authenticated directories, root-owned and not group/world
writable. Existing regular outputs are root:root with no xattrs; the three links
are root:root 0777. The locale alias is never followed by a write. Unknown paths,
changed parent types, substituted links and unsupported output metadata remain
rejected. A partial manifest-backed fixture retains the actual metadata and public
Files bytes; its ordinary-directory writer tests map ownership to the test runner.
It is not another artifact or native qualification.

The earlier locale fix is retained. The reviewed configuration plan now serializes
`TransformationPolicy = debian-trixie-core-configuration-v2` and includes it in its
fingerprint. The native profile rejects a missing or different policy. Writer and
observer use the same fixed Files declaration. Windows v1 fingerprints are unchanged.

## Helpers and ordering

Files produces mailname, Exim input, locale selection and hostname/hosts before their
consumers run. Exim's local configuration and generated macro checks agree with the
retained `update-exim4.conf` version. Its temporary output must disappear after the
successful rename; no persistent wildcard output is permitted.

Debconf's retained File driver saves dirty databases, renames the prior config to
`config.dat-old`, and removes `config.dat-new` by rename. The observer permits only
the two identity selections and declared flag, and compares the backup with the
original stanza set. Unrelated templates/password databases remain in the unchanged
complement. TLS's noninteractive snakeoil path uses the configured isolated hostname,
the retained ssl-cert template and OpenSSL. Its exact key/certificate and verified
hash link are the only declared persistent TLS outputs; private `/tmp` supplies
temporary files. The authenticated `ssl-cert` group is GID 105.

Locale generation has the required source/charmap. Its input
`/usr/share/locale/locale.alias` is an authenticated link to `/etc/locale.alias`, now
explicitly reviewed as a read alias. It is not a writable alias. The output archive
is absent initially and must contain only the declared generated locale. The
read-only archive query uses the supported positional archive argument in
[glibc 2.41](https://raw.githubusercontent.com/bminor/glibc/glibc-2.41/locale/programs/localedef.c).

The account validator confirms UID/GID 1000 is unused, sudo has no members and root
is locked. The five skel descendants include `.face.icon -> .face`; these are exact
home-copy expectations. Account databases and backups retain their observed owner
and mode, consistent with the pinned shadow version's
[backup implementation](https://raw.githubusercontent.com/shadow-maint/shadow/4.17.4/lib/commonio.c).
Only the declared principal and sudo relationship may change. Temporary account
locks must be absent afterward; the existing empty `.pwd.lock` remains unchanged.
Credential delivery remains a sealed, secret-free journal reference.

The static ELF review includes sudo's declared `/usr/libexec/sudo` library search
path. Shell interpreters and merged-/usr aliases resolve through authenticated
manifest entries. This does not run a loader against the target. Dynamic Perl,
OpenSSL and NSS behavior still requires execution evidence; the full baseline and
unchanged-complement checks cover their installed files before each helper release.

The actual artifact has no `/boot/efi`. The corrected private helper view masks
`/boot` read-only and uses the verified empty `/mnt` directory as its alias. It does
not create target scaffolding or mount the real ESP. Native stopped-gate observations
and current view fixtures remain required. Read-only review cannot prove helper
success, generated output semantics, effective isolation or canonical teardown.

## Executed attempt and subsequent correction

Run `16dd9a84-02e0-402a-a208-89555f1e8f15`, operation
`e21b1026-2ca6-4308-b546-55ffd174f682`, session
`bb04e4fe-4a90-4c4a-9410-e0a928e278e5` used the existing derivation mechanism.
Its independent copies matched the protected successful-import checkpoint. Fresh
canonical ownership, journal placement, acquired roles and mounted baseline passed.
Five current native helper-view fixtures passed; the same entrypoint rejected a
wrong predecessor before target block acquisition. The actual configuration then
passed independent Files and Debconf semantics, whole-tree complement and package
readback. The locale link was preserved and its direct file target was written.

Exim's intent was durably published. Readback did not pass and its result remains
OutcomeUnknown. A separate read-only debugfs diagnostic found the generated 25,448-byte
regular file with UID 0, GID 104 and mode 0644, and no temporary output. The retained
helper's `gentmpconf` explicitly sets `root:Debian-exim` before its final rename.
The pre-attempt review did not cover that ownership assignment correctly. This is
a review/observer defect, not an external prerequisite or a reason to relax ownership.

Final source uses `verify_exim` with the authenticated baseline's `Debian-exim`
group, requires exactly GID 104, and rejects root-group, unrelated-group or permissive
mode output. The diagnostic now explicitly reports generated owner/group/mode and
includes the helper's additional shell utilities in the static dependency closure
(81 objects). Final `TransformationPolicy` is **v3**; the consumed native attempt
ran **v2**. The final correction has not executed on a full target.

Eleven session and eight effect records were independently reopened. The last
verified effect is Debconf. TLS, locale generation, account, credential and sudo
operations were not reached. No complete configured-state result or canonical
teardown/Close exists. Supervisor/namespace disappearance is not teardown evidence.
Ordinary provisioning unmount and normal runtime shutdown are recorded separately.
The powered-off partial target is retained and is not an initramfs-ready handoff.

See [new native evidence](debian-core-configuration-reviewed-attempt-evidence.json)
for executed versus final source hashes and retained record references. The
authorization/reservations are consumed. A subsequent native experiment needs
separate explicit authorization; neither this correction nor a checkpoint grants
replay authority. ProductionAuthentication remains Unsupported and NativeSupported
remains zero.
