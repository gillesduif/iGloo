# V5 configuration qualification: host-capacity stop (2026-09-29)

The newly authorized qualification did **not reach a helper pass or canonical
configuration**. V5's narrow useradd-defaults correction remains implemented and
regression-tested, but not native-qualified. Earlier helper and canonical failures
are unchanged. See [this continuation's evidence](debian-core-v5-capacity-block.json).

## Attempt budget and actual stop

The explicit budget was up to three fresh retained-root helper passes, followed by
one canonical attempt only after the full helper sequence passes on the intended
execution revision. Neither budget has been consumed: helper passes 0/3, canonical
attempts 0/1. The host provisioning failure is retained separately; it must not be
resumed or overwritten.

The new fixture preparation directory is
`/var/lib/igloo/helper-contract-labs/6735aa795c494aee88d07a7c5bd7540d/`.
It contains the budget, preservation observations, creation intent, completed
independent runtime copy and incomplete checkpoint-source copy. The producer
stopped in `copy_verified` when `cp` returned an input/output error reading the
checkpoint `target.raw`. No QEMU launch, helper dispatch reservation or canonical
authorization was reached. Do not treat the incomplete `source.raw` as usable.

The source's full expected hash check passed immediately before copying. The
initial preservation scan covered the protected checkpoint, three prior failed
canonical backing pairs and the previous helper runtime/source. These raw records
were written before the failure. They are currently unavailable for independent
post-failure reopen because WSL cannot start. No post-failure preservation success
is claimed, and no original backing was intentionally written.

WSL had reported approximately 616 GiB available in its virtual filesystem. The
subsequent Windows observation found **zero free bytes on C:**; WSL then reported
`Wsl/Service/CreateInstance/E_FAIL`, error code 6, failure step 2. A later observation
found approximately 4 GiB free, still below the declared 180 GiB task reserve. The
full underlying volume and WSL startup failure are confirmed. Storage exhaustion
is the likely explanation for the copy error; no guest/kernel diagnostic was
obtained to establish that causal chain conclusively.

No files were deleted to free space. No WSL shutdown/restart, filesystem repair,
VHD compaction/resize, host reboot or retry was issued. The automatic WSL startup
attempt made by the read-only diagnostic failed; it was not a qualification VM.

## Narrow preflight correction

Checking only WSL `statvfs` does not establish capacity on the Windows volume
containing its dynamically growing VHD. The new read-only
`Test-CanonicalLabHostCapacity.ps1` resolves the nominated WSL2 distribution's
registered VHD placement and reads the fixed Windows volume's available space.
Unknown/ambiguous distribution, unsupported placement, reparse points or unavailable
capacity fail closed. It neither starts WSL nor opens guest filesystems or grants
mutation authority. Private registry/backing paths are omitted from diagnostics.

`canonical_lab_capacity.require_backing_capacity` invokes that bounded observation
for the existing WSL lab host. `copy_verified` checks it before hashing/copying, and
`canonical_storage_lab.create` checks before creating a run directory. Existing
filesystem capacity, ownership, derivation, create-new and independent hash checks
remain required. Native Linux retains its existing filesystem checks. A passing
capacity observation is time-specific and cannot guarantee that unrelated host
activity will not subsequently consume space.

The real Windows preflight returned exit 2 / `Sufficient=false`. Five mocked
protocol/ordering tests passed on Windows; the WSL-to-Windows invocation itself
could not be executed after WSL became unavailable. The Windows-only unit shim for
the unavailable `pwd` module provides no provider lookup or storage authority.

V5's effect/observer code and public policy binding were not changed. Four new
synthetic account-observer cases cover step-relative backups, unrelated account
changes, credential substitution, root unlocking, modes and sudo membership.
They do not establish native User/Credential/Sudo behavior.

## Next permitted work

First make sufficient underlying Windows capacity available without deleting or
changing retained iGloo evidence, and restore availability of the existing WSL
environment through an explicitly controlled host action. This task did not perform
that host recovery. Then independently reopen the protected records, verify the
checkpoint and all retained resources, and inspect the incomplete provisioning
directory without resuming it. Create a new independent fixture preparation under
the remaining authorization, repeat the capacity and source checks, and run v5
from Baseline. Only a complete passing helper sequence may precede the one canonical
authorization. No configured target or initramfs handoff exists from this run.

Prior failed-session teardown remains an acceptance backlog item. Production
authentication stays Unsupported; NativeSupported stays 0. Windows v1, recovery
and readiness semantics are unchanged. No import replay, target formatting,
initramfs, agent, boot or firmware operation occurred.
