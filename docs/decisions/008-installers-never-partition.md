# ADR-008: Installers never partition the shared disk — iGloo pre-creates everything

**Status:** Accepted
**Date:** 2026-07-11

## Context

Three separate installer stacks each offered an "automatic partitioning"
convenience, and each one, when used naively, meant **whole-disk wipe**:
Anaconda's unbounded `clearpart`, debian-installer's `partman-auto/method`
(which silently overrides `biggest_free`), and subiquity's `layout:` presets.
Worse, curtin storage v2 treats its config as *authoritative* — partitions not
declared get **deleted** — and when asked to *create* a partition it rewrites
the entire GPT: new disklabel GUID, renumbered entries, clobbered type GUIDs
and PARTUUIDs unless every field is fed back explicitly, followed by a
kernel-table reload that fails while live-media partitions on the same disk are
in use. Windows survived two of these incidents by luck alone (a busy partition
aborted the wipe mid-flight).

## Decision

The installer is never allowed to make partitioning decisions on the shared disk:

1. **iGloo creates all partitions from Windows** (diskpart): seed, ISO partition
   when needed, and — for curtin-based distros — the Linux root partition itself
   (`EnsureRootPartition`, GPT type `0FC63DAF…`, unformatted).
2. **Installer configs declare every partition with full identity** —
   `number/offset/size/partition_type/uuid`, all `preserve: true` — generated
   from the real disk at staging time (`BuildStoragePartitionList`). The
   installer only *formats and mounts* the root iGloo made.
3. Free-space mechanisms that are provably safe (partman `biggest_free` with
   `method` unset; guarded kickstart `clearpart`) remain allowed for the
   installers that honour them.

## Rationale

- Partitioning is the single highest-consequence operation in the product
  (BR-01/BR-03); it belongs in the one code path we fully control and test,
  not in four differently-quirked installers.
- A config that *describes reality exactly* makes any residual table rewrite
  byte-identical — harmless by construction.

## Consequences

- **Positive:** the "installer wiped the disk" failure class is closed
  structurally, not by configuration discipline.
- **Positive:** iGloo's Windows-side partitioner was already trusted code
  (it made the seed/ISO partitions for three validated distros).
- **Negative:** per-distro storage configs need real geometry plumbed in
  (token substitution at staging time) — more moving parts on the Windows side.
- **Open:** curtin still runs `sfdisk`+`partprobe` unconditionally even for
  all-preserved configs (verified in its source); the remaining Ubuntu work is
  keeping the disk's partitions unheld at that moment (ADR-009 / STATUS.md).
