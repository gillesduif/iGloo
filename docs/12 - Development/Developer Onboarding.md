---
title: Developer Onboarding
type: moc
product: project
status: mixed
doc_status: current
validation_status: partial
availability: not-applicable
graph_cluster: delivery
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [CONTRIBUTING.md, docs/reference/building.md, Igloo.sln, docs/spec/command-contract/README.md]
aliases: []
tags: [igloo]
---

# Developer Onboarding

Start with [CONTRIBUTING](https://github.com/gillesduif/iGloo/blob/main/CONTRIBUTING.md), [building](../reference/building.md) and [repository structure](../reference/repository-structure.md). This checkout contains uncommitted work; record branch, HEAD and dirty state before changing anything. For architecture changes, read [[02 - Architecture/Current Architecture]], [[02 - Architecture/Target Architecture]] and [[00 - Home/Architecture Drift Register]] rather than relying on the older root README alone.

To add a public capability, implement a typed handler below Host, review risk/authority and data minimization, update [canonical schemas](../spec/command-contract/README.md), discovery, human/machine routing and focused process tests. Keep private manifests and secrets out of argv/results. Do not register destructive migration, install or recovery commands before [[10 - Safety and Security/Safety and Security Overview]] gates and [[13 - Testing and Validation/Test Matrix]] acceptance evidence are satisfied. To add a provider, use the [distribution guide](../reference/adding-a-distribution.md) and preserve Core-only dependency direction; a distro-owned frontend is a separate CLI consumer.

The contribution guide requires an issue before a large change and disposable VM validation for runtime-relevant installation, boot, first-boot or ISO verification changes. A green build or fixture suite does not replace the specified VM/hardware evidence. [[15 - Implementation/Master Implementation Checklist]] holds proposed convergence tasks; GitHub [milestones](https://github.com/gillesduif/iGloo/milestones) hold release planning.
