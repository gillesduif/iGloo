---
title: Community Overview
type: moc
product: community
status: in-progress
doc_status: current
validation_status: partial
availability: unknown
graph_cluster: community
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [src/Igloo.Community.App/Igloo.Community.App.csproj, docs/architecture/community-installation-ownership.md, README.md]
aliases: []
tags: [igloo]
---

# Community Overview

iGloo Community is the WPF migration wizard and the product represented by the public [0.2-alpha release](https://github.com/gillesduif/iGloo/releases). **CODE OBSERVATION:** The local dirty branch still references `Igloo.Core.Host` and Command.Contracts directly. Host interfaces support preflight, ISO/cache, private migration staging, USB, installation preparation and Windows maintenance. The private `MigrationManifest` cannot be put into the public machine protocol merely to remove a project edge.

**NORMATIVE TARGET:** Community becomes a graphical client of the CLI process for endpoint capabilities (whitepaper A2, §12.1). It must keep the same guarded semantics and local sensitive-input custody. The transition is blocked by missing exact complete layout, secret and approval handling, recovery proof, durable migration continuation and postcondition verification. [[02 - Architecture/Architecture Convergence Plan]] AC-03/09 orders parity before Host removal; [[10 - Safety and Security/Safety and Security Overview]] names the safety gates.

[Community installation ownership](../architecture/community-installation-ownership.md), [shared orchestration](../architecture/shared-orchestration.md), [existing Community map](../community.md) and [browser migration](../reference/browser-migration.md) are detailed sources. Their validation dates must stay attached to the claim. A released earlier installer and a new branch-only CLI contract are separate facts. [[13 - Testing and Validation/Test Matrix]] describes what must be requalified.
