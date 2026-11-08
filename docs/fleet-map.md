---
title: Fleet control plane
type: hub
area: fleet
status: current
implementation: not-applicable
tags:
  - igloo
  - topic/assessment
aliases: []
reviewed: 2026-10-03
reviewed_commit: 5e4ebe846b488d8609612d37665aaefd4d203c88
sources:
  - src/Igloo.Fleet.Contracts/PlanningProtocol.cs
  - src/Igloo.Fleet.Agent/Execution/PreCommitGate.cs
  - src/Igloo.Fleet.Domain/MigrationEvidence.cs
  - docs/fleet/architecture.md
---

# Fleet control plane

iGloo Fleet is the organisation product: a European answer to Microsoft's endpoint administration for enterprises and governments that migrate their Windows PCs to Linux and keep managing them from one console. Today it collects endpoint assessments and supports audited operator planning; approved execution on the endpoint and the management modules are later stages. The code lives in [gillesduif/iGloo-Fleet](https://github.com/gillesduif/iGloo-Fleet) since 2026-11-07 ([[decisions/016-fleet-source-available-and-contract-apache|ADR-016]]). Its [architecture](fleet/architecture.md) shows Agent, Server, Domain, Persistence, Contracts and Web. The [product boundary](architecture/product-boundaries.md) and [current state](current-state.md) explain why a Prepared plan does not execute Community's migration.
As one of the four peer products in [[iGloo Home|the product family]], Fleet may reuse selected Platform modules while keeping organization approval and sanitized evidence distinct from [[community|Community]]'s private local migration state. [[decisions/013-product-family-and-platform|ADR-013]] records that ownership boundary. AI and CLI do not add an execution endpoint to Fleet's current protocol.

## Follow the read-only path

The [Phase 0 development demonstration](fleet/phase-0.md) is historical prototype context. [Phase 1 operations](fleet/phase-1.md) and its [inspected baseline](fleet/phase-1-baseline.md) describe the trusted engineering path. [Enrollment](fleet/enrollment.md) establishes identity; [migration profiles](fleet/migration-profiles.md) define operator intent; [dry-run](fleet/dry-run.md) assesses compatibility. [Persistence and evidence](fleet/persistence.md) hold the resulting history. [Security](fleet/security.md) explains authorization and sanitized payload limits.

The [foundation implementation report](fleet/implementation-report.md) and [Phase 1 report](fleet/phase-1-implementation-report.md) are dated records. Read their test totals as results of those runs, not tests run for this vault update.

## Execution boundary

[Phase 2](fleet/phase-2.md) records partial exact-target, recovery and guarded execution design. `RunAssessment` and `RunMigrationDryRun` are the only Fleet work types in `PlanningProtocol.cs`. `PreCommitGate.cs` still supplies production `RecoveryReadiness = ObservationUnavailable / NotImplemented`. The [shared recovery snapshot](architecture/recovery-snapshot-v1.md) is read-only; it does not authorize restoration or migration.

Fleet's [MigrationEvidence](fleet/persistence.md) is sanitized. The private, local Community manifest remains on the [Community side](community.md). The [safety map](safety-and-evidence.md) connects this data boundary to evidence and hardware validation.
