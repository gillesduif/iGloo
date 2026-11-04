---
title: Core and Runtime Overview
type: moc
product: core
status: mixed
doc_status: current
validation_status: partial
availability: branch-only
graph_cluster: core
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [src/Igloo.Core.Host/IglooHost.cs, src/Igloo.Core.Runtime/CommandDispatcher.cs, src/Igloo.Core.Runtime.Execution/OperationJournal.cs]
aliases: []
tags: [igloo]
---

# Core and Runtime Overview

Logical iGloo Core is the endpoint execution authority, even though its implementation spans several assemblies. **CODE OBSERVATION:** `Igloo.Core.Host` composes Command.Contracts, Runtime, Runtime.Execution, Core, Preflight, Iso, Migration and UsbWriter. Runtime holds the typed dispatcher and read-only/advisory handlers. Runtime.Execution contributes low-risk acquisition, status, target and allocation handlers. Distribution providers supply installer-specific behavior below Core. [[02 - Architecture/Current Architecture]] shows physical references; [[02 - Architecture/Target Architecture]] shows the intended executable boundary.

Host supports both CLI composition and transitional private Community/Fleet workflows. That distinction is central: a private Host method is not a registered public capability. The current ISO journal and selected cross-process leases give limited durability; no public migration OperationId spans reboot. `recovery.inspect` returns unavailable exact readiness. [[03 - CLI and Command Contract/Current Capability Registry]] names the public handlers; [[10 - Safety and Security/Safety and Security Overview]] records the closed mutation gates.

For deeper canonical material, read [shared orchestration](../architecture/shared-orchestration.md), [Community installation ownership](../architecture/community-installation-ownership.md), [backend boundary audit](../architecture/backend-cleanup-audit-2026-10-06.md), [recovery snapshot](../architecture/recovery-snapshot-v1.md) and the [command implementation report](../spec/command-contract/implementation-report.md). Their dates and scope differ; do not infer that a 6 October audit describes every 10 October working-tree change.
