---
title: Data and Contracts Overview
type: moc
product: platform
status: mixed
doc_status: current
validation_status: partial
availability: branch-only
graph_cluster: cli
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [src/Igloo.Command.Contracts/CommandProtocol.cs, docs/spec/command-contract/README.md, src/Igloo.Fleet.Contracts/PlanningProtocol.cs]
aliases: []
tags: [igloo]
---

# Data and Contracts Overview

The public endpoint contract is the [V1 Command Contract](../spec/command-contract/README.md), implemented by `Igloo.Command.Contracts` and handlers. It separates typed request, result, discovery and stream-event envelopes. Protocol version, capability version, schema version and CLI product version are separate dimensions. `CommandResult.succeeded` and stable error code govern domain outcome; stderr and process exit are transport diagnostics. [[03 - CLI and Command Contract/Command Contract V1]] summarizes usage and [[03 - CLI and Command Contract/Current Capability Registry]] lists registration.

Private `MigrationManifest` and local user data do not belong in public results or Fleet transport. Advisory PlanId, ArtifactId, TargetId and limited AllocationId name different non-authorizing facts; [[10 - Safety and Security/Plan Target Allocation and Artifact Bindings]] states their current scope. `OperationId` currently covers ISO acquisition records, not a public migration lifecycle. Approval, RecoveryId and SecretHandle are target concepts without public implemented capability.

Fleet's [planning protocol](../fleet/architecture.md) is a separate network/control-plane contract with sanitized evidence, not a second endpoint execution API. Provider manifests and interfaces are another separate contract below Core. A future change must update the canonical schema/serializer, capability discovery, error mapping, tests and this vault together; [[20 - Reference/Vault Maintenance]] describes that review loop.
