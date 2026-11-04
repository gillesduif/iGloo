---
title: Architecture Source Conflicts
type: decision-register
product: platform
status: proposed
doc_status: current
validation_status: unverified
availability: not-applicable
graph_cluster: architecture
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [docs/decisions/013-product-family-and-platform.md, docs/spec/command-contract/README.md, docs/architecture/product-boundaries.md]
aliases: []
tags: [igloo]
---

# Architecture Source Conflicts

No new accepted ADR was created for this vault pass. [ADR-013](../decisions/013-product-family-and-platform.md) accepts the product-family target; [ADR-004](../decisions/004-plugin-architecture.md) records provider direction. The supplied whitepaper A2 goes further by requiring a single CLI **process** gateway, while current Command Contract and Community/Fleet code retain in-process Host paths. [[00 - Home/Architecture Drift Register]] DR-01/02 is the evidence register; AC-01 in [[15 - Implementation/Master Implementation Checklist]] is the canonical proposed decision task.

An ADR should decide: the public executable boundary, exactly which transitional Host paths remain private, how parity is proven before removal, whether the transport-only CLI Client is an endorsed first-party helper, and how protocol/capability changes are governed. Separate proposals should later address approval artifact semantics, durable journal ownership and provider trust. Code patterns and PDF prose are evidence/intent, not substitutes for accepted decision status. Existing ADR IDs and historical wording remain intact.
