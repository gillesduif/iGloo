---
title: Release Overview
type: moc
product: project
status: mixed
doc_status: current
validation_status: partial
availability: released
graph_cluster: delivery
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [README.md, ROADMAP.md, RELEASE-NOTES.md, OSS_RELEASE_CHECKLIST.md]
aliases: []
tags: [igloo]
---

# Release Overview

The public [GitHub Releases page](https://github.com/gillesduif/iGloo/releases) lists **iGloo 0.2-alpha** as a prerelease. Its Community installer and documented limitations are separate from the uncommitted CLI/One/Fleet/Core working tree at `366c1ec`. No claim here says the CLI Client, One shell or advisory target/allocation commands ship in 0.2-alpha. [Release notes](https://github.com/gillesduif/iGloo/blob/main/RELEASE-NOTES.md), [changelog](https://github.com/gillesduif/iGloo/blob/main/CHANGELOG.md) and [OSS release checklist](https://github.com/gillesduif/iGloo/blob/main/OSS_RELEASE_CHECKLIST.md) remain canonical public/release records.

[ROADMAP](https://github.com/gillesduif/iGloo/blob/main/ROADMAP.md) links the actual GitHub milestones: M13 detection/removal, M15 boot menu, M16 pre-install snapshot/rollback, M17 UI, M18 closed beta and M19 public 1.0. These are the repository's release planning sources, not an assignment of the vault's proposed AC tasks. Current milestone state should be checked on GitHub before external commitments. [[15 - Implementation/Master Implementation Checklist]] keeps the architecture proposal distinct.

Before packaging branch CLI/clients, verify binary placement, product identity/protocol negotiation, compatibility, trust/signing policy, installer/first-boot evidence by distribution and hardware, failure diagnostics, support escalation and rollback scope. [[13 - Testing and Validation/Test Matrix]] and [[10 - Safety and Security/Safety and Security Overview]] set the gates. Documentation completeness is not release readiness.
