---
title: Current Capability Registry
type: capability-register
product: cli
status: mixed
doc_status: current
validation_status: partial
availability: branch-only
architecture_state: current
source_authority: code-observation
graph_cluster: cli
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [src/Igloo.Core.Runtime/CommandDispatcher.cs, src/Igloo.Core.Host/IglooHost.cs, docs/spec/command-contract/capabilities.schema.json]
aliases: []
tags: [igloo]
---

# Current Capability Registry

**REGISTERED CAPABILITY:** The normal CLI Host factory registers the following 19 distinct public IDs in the inspected 10 October working tree. `migration.plan` has versions 1 and 2, so discovery has 20 ID/version descriptors. This was cross-checked against `CommandDispatcher`, `IglooHost` handler composition and [capability schemas](../spec/command-contract/capabilities.schema.json). The read-only Host factory omits `iso.acquire` and `artifact.acquire`; actual provider availability is discovered per installed catalogue. `Igloo.Cli.Client` and One are uncommitted working-tree projects at HEAD `366c1ec`.

| Capability ID | Public/private | Class | Registered in normal Host? | Execution semantics | Risk | Validation scope | Target gap | Source |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `system.version`, `system.capabilities`, `system.capability.describe` | public | query | yes | version and descriptor discovery | none | source and focused tests inspected | package identity policy | `CommandDispatcher.cs` |
| `system.inspect`, `system.inspect.detail` | public | query | yes | sanitized summary/detailed endpoint facts | low | mapper and command tests inspected | exact mutation identity remains separate | `CommandDispatcher.cs` |
| `distro.list`, `distro.inspect`, `distro.compatibility` | public | query | yes | installed provider catalogue and advisory compatibility | none/low | source/tests inspected | provider conformance | `CommandDispatcher.cs` |
| `migration.inspect` | public | query | yes | read-only migration facts | low | source/tests inspected | execution admission | `MigrationReadOnlyHandlers.cs` |
| `migration.plan` v1/v2 | public | plan | yes | advisory only; `executable: false`; v2 persists PlanId | low | source/schema/tests inspected | exact full intent and approval | `MigrationReadOnlyHandlers.cs`, `AdvisoryPlanJournal.cs` |
| `migration.plan.read` | public | query | yes | reopens advisory v2 plan | low | source/schema inspected | no execution authority | `AdvisoryPlanJournal.cs` |
| `migration.target.bind` | public | plan | yes | opaque canonical boot-disk TargetId, non-executable | low | source/schema/tests inspected | full layout and approval | `MigrationTargetBinding.cs` |
| `migration.target.allocate` | public | plan | yes | advisory NTFS shrink intent V1, non-executable | low | source/schema/tests inspected | does not bind complete installer layout | `MigrationAllocationBinding.cs` |
| `recovery.inspect` | public | query | yes | exact readiness unavailable | low | source and tests inspected | independent recovery proof | `MigrationReadOnlyHandlers.cs` |
| `operation.status`, `migration.status` | public | query | yes | journal status; no public migration producer | low | source/tests inspected | cross-reboot migration lifecycle | `OperationJournal.cs` |
| `iso.acquire`, `artifact.acquire` | public | low-risk cache mutation | yes | verified provider-scoped ISO acquisition; no disk install | low | source/tests inspected; no live download here | durable installer binding | `IsoAcquireHandler.cs` |
| `artifact.status` | public | query | yes | opaque artifact metadata/status | low | source/schema inspected | no execution authority | `ArtifactRegistry.cs` |

No `migration.prepare`, `migration.execute`, `migration.verify`, installation/boot mutation, recovery mutation, SecretHandle, RecoveryId or migration OperationId producer is registered. A `PlanId`, `TargetId`, advisory `AllocationId` or `ArtifactId` is **not** an approval. Private Community `MigrationManifest` and Host workflows are not public commands. [[10 - Safety and Security/Plan Target Allocation and Artifact Bindings]] explains identity scope. [Command Contract V1](../spec/command-contract/README.md) and [[03 - CLI and Command Contract/Command Contract V1]] define current envelopes; [[13 - Testing and Validation/Test Matrix]] distinguishes inspected test code from executed tests.
