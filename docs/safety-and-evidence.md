---
title: Safety and evidence
type: hub
area: safety
status: current
implementation: not-applicable
tags:
  - igloo
  - topic/safety
  - topic/evidence
aliases: []
reviewed: 2026-10-03
reviewed_commit: 5e4ebe846b488d8609612d37665aaefd4d203c88
sources:
  - docs/reference/safety-model.md
  - docs/reference/hardware-findings.md
  - docs/architecture/recovery-snapshot-v1.md
  - docs/architecture/installer-esp-binding.md
  - src/Igloo.Fleet.Agent/Execution/PreCommitGate.cs
---

# Safety and evidence

This map separates a safety *design*, its implementation, and proof from a particular run. [Business rules](business/business-rules.md) and the [risk register](business/risk-register.md) state the intended constraints. The older [safety model](reference/safety-model.md) is useful design context; the [current state](current-state.md) and the newer exact-target audits control current capability claims.

## Disk, firmware and recovery

The [Community ownership review](architecture/community-installation-ownership.md) and [installer ESP audit](architecture/installer-esp-binding.md) identify the exact disk, partition and ESP bindings needed before mutation. [ADR-008](decisions/008-installers-never-partition.md) preserves the historical reasoning, while [target-root deployment](architecture/target-root-deployment.md) explains the later guarded design. The [shared recovery snapshot](architecture/recovery-snapshot-v1.md) is read-only; [Fleet Phase 2](fleet/phase-2.md) records why production readiness still returns unavailable.

## Data and trust boundaries

The [Community/Fleet product boundary](architecture/product-boundaries.md#execution-artifacts-are-not-fleet-records) distinguishes local private `MigrationManifest` from sanitized Fleet `MigrationEvidence`. [Fleet security](fleet/security.md) documents allowlisted assessment fields and authorization. [Browser migration](reference/browser-migration.md) and [ADR-011](decisions/011-chromium-credential-migration.md) explain Community's sensitive credential path. [Password hashing](reference/password-hashing.md) covers installer credentials.

The four-product [[architecture/product-boundaries|trust boundary]] treats UI, CLI, AI and Fleet input as untrusted until validated by iGloo. [[cli|CLI]] must not take passwords in ordinary arguments; [[ai|AI]] must not receive secrets or issue arbitrary system commands. [[architecture/shared-orchestration|Plan validation]] and [[architecture/migration-rules|file-rule preview]] are designs, not current guards. A destructive confirmation must bind to the actual target and current observations; do not treat an AI suggestion or Fleet Prepared plan as consent.

## What evidence proves

The [hardware register](reference/hardware-findings.md) records observed July–August 2026 physical runs and their guards. It does not validate every later commit or configuration. The [Debian target-root record](architecture/debian-target-root-deployment.md) links protected JSON evidence for bounded lab stages; those JSON bytes are retained artifacts, not notes to reformat. The [Debian session boundary](architecture/debian-session-boundary.md) distinguishes completed canonical teardown from ordinary unmount, power-off and process absence.

For release or implementation work, follow the [development map](development.md) and state the exact command, environment, revision, and outcome. Do not promote a fixture, source inspection, or an earlier test count into present hardware readiness.
