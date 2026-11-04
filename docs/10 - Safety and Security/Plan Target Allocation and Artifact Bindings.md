---
title: Plan Target Allocation and Artifact Bindings
type: safety
product: core
status: blocked
doc_status: current
validation_status: partial
availability: branch-only
graph_cluster: safety
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [docs/spec/command-contract/README.md, src/Igloo.Core.Runtime.Execution/MigrationTargetBinding.cs, src/Igloo.Core.Runtime.Execution/MigrationAllocationBinding.cs, docs/spec/command-contract/implementation-report.md]
aliases: []
tags: [igloo]
---

# Plan Target Allocation and Artifact Bindings

The supplied whitepaper §7.4 distinguishes intent, verified bytes, physical target, exact allocation, recovery, secret, approval and durable execution identities. **CODE OBSERVATION:** This checkout has advisory PlanId, ArtifactId, TargetId and `AllocationId` V1 records. They remain distinct and non-authorizing. [[03 - CLI and Command Contract/Current Capability Registry]] lists their public commands.

| Identity | Current meaning | What it does not prove |
| --- | --- | --- |
| PlanId | Persisted advisory `migration.plan` v2 intent and observation revisions | Target choice, approval, safe execution |
| ArtifactId | Provider-scoped verified ISO/cache bytes | Installer layout, authority or bootability |
| TargetId | Opaque binding to canonical Windows boot-disk identity, revalidated against observations | Future partition geometry or approved mutation |
| AllocationId V1 | `WindowsBootNtfsShrinkIntentV1` with a bounded planned shrink and layout revision | Complete stage-2/seed/ISO layout or executor consumption of that geometry |
| RecoveryId, SecretHandle, execution approval, migration OperationId | Not public capabilities today | Nothing may be inferred from the planned names |

The current allocation record is **not** the complete target allocation anticipated by whitepaper §7.4. The direct-install path and provider renderers still need a single exact writer/readback and installer-consumed geometry, artifact/provider revalidation, and cross-reboot continuation. [Implementation report](../spec/command-contract/implementation-report.md) records the specific partial fixture work. No public `migration.prepare` may treat current IDs as a complete admission ticket.

At destructive commit time, Core must independently reobserve current machine/partition state, canonical target, allocation/layout, artifact revisions, risk-appropriate recovery evidence, protected secret availability and approval of the exact intent. It must acquire exclusive resource ownership and create durable OperationId before irreversible work. A button, model recommendation, signed Fleet job or `--yes` is only a request (whitepaper A8). [[10 - Safety and Security/Safety and Security Overview]] and [[02 - Architecture/Architecture Convergence Plan]] track these closed gates.
