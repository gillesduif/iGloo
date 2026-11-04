---
title: Development and validation
type: hub
area: delivery
status: current
implementation: not-applicable
tags:
  - igloo
  - topic/validation
aliases: []
reviewed: 2026-10-03
reviewed_commit: 5e4ebe846b488d8609612d37665aaefd4d203c88
sources:
  - CONTRIBUTING.md
  - docs/reference/building.md
  - docs/code/README.md
  - .github/workflows
---

# Development and validation

Use the repository-root `CONTRIBUTING.md` rules before runtime changes. The [build guide](reference/building.md), [code tour](code/README.md), and [repository structure](reference/repository-structure.md) help locate work. The [shared engine map](shared-engine.md) explains the project boundaries that tests must preserve.

## Validation layers

The solution and tests cover code behavior. Installer, first-boot, boot entry and ISO changes require a real end-to-end VM run under `CONTRIBUTING.md`; a build or fixture is insufficient. [Hardware findings](reference/hardware-findings.md) are historical observations with their own dates. [Safety and evidence](safety-and-evidence.md) explains why separate evidence levels remain separate.

[Fleet Phase 1 operations](fleet/phase-1.md) and [its implementation report](fleet/phase-1-implementation-report.md) describe one engineering demonstration. [Fleet Phase 2](fleet/phase-2.md) records later guarded and blocked work. The [Debian lab records](architecture/debian-target-root-deployment.md) have distinct native, fixture and canonical outcomes; read the specific checkpoint before attempting a continuation.

## Documentation maintenance

Use the [Graph Guide](vault/graph-guide.md) for navigation and the [vault audit](vault/audit.md) for the baseline, validator limitations and update checklist. New notes should follow the [note template](vault/note-template.md), but do not replace useful existing prose merely to standardize metadata.
