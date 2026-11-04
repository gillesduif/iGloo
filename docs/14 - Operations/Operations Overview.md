---
title: Operations Overview
type: moc
product: project
status: mixed
doc_status: current
validation_status: partial
availability: branch-only
graph_cluster: delivery
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [docs/spec/command-contract/README.md, docs/fleet/phase-1.md, docs/fleet/phase-2.md]
aliases: []
tags: [igloo]
---

# Operations Overview

For the branch CLI, first verify the intended `Igloo.Cli.exe`, protocol hello and capability discovery using the [current Command Contract](../spec/command-contract/README.md). The Community binary `iGloo.exe` is different. A missing/substituted binary, incompatible protocol, malformed stdout, mismatched request IDs or unexpected process death is a **transport failure**. Do not turn stderr or exit code into a successful endpoint result. [[03 - CLI and Command Contract/Command Contract V1]] describes the current framing.

If an ISO acquisition loses its client process after acceptance, use `operation.status` with its known OperationId through a new CLI process and reconcile verified artifact state; the [spec](../spec/command-contract/README.md) documents the supported journal scope. If no durable ID is available, outcome remains unknown. Do not retry a future destructive command based on a timeout alone. Stale-state, corrupt evidence or resource-busy errors call for fresh observation and diagnosis, not bypassing the gate.

There is **no qualified public migration cross-reboot recovery runbook**: `recovery.inspect` cannot attest exact readiness and no public migration operation producer exists. For private Community or Fleet incidents, follow their existing dated [safety model](../reference/safety-model.md), [hardware findings](../reference/hardware-findings.md), [Fleet operations](../fleet/phase-1.md) and [Phase 2 blocker](../fleet/phase-2.md); preserve local protected diagnostics and escalate when exact state cannot be established. Do not invent a restoration command. [[10 - Safety and Security/Safety and Security Overview]] tracks prerequisites for a future supported procedure.
