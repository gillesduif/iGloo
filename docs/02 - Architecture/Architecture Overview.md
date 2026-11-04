---
title: Architecture Overview
type: moc
product: platform
status: mixed
doc_status: current
validation_status: partial
availability: branch-only
graph_cluster: architecture
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [Igloo.sln, docs/architecture/product-boundaries.md, docs/spec/command-contract/README.md]
aliases: []
tags: [igloo]
---

# Architecture Overview

Read the architecture in three passes: [[02 - Architecture/Current Architecture]] records the 10 October working tree; [[02 - Architecture/Target Architecture]] describes the supplied whitepaper's normative CLI-mediated design; and [[02 - Architecture/Architecture Gap Analysis]] connects them to [[00 - Home/Architecture Drift Register]], safety gates and [[02 - Architecture/Architecture Convergence Plan]]. [[02 - Architecture/Invariant Conformance Matrix]] records A1–A10 with code and test gaps.

The key distinction is physical Host composition versus public executable gateway. The CLI currently calls Host's dispatcher, while Community and Fleet Agent retain direct Host paths. One's read-only/planning shell uses a CLI process client. Fleet Server has no endpoint execution dependency. Providers extend below logical Core; external and distro-owned frontends consume CLI above it. Neither an advisory PlanId nor a compiled handler proves safe migration execution. [[04 - Core and Runtime/Core and Runtime Overview]], [[03 - CLI and Command Contract/CLI and Command Contract Overview]] and [[10 - Safety and Security/Safety and Security Overview]] provide the next reading path.
