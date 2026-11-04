---
title: iGloo Home
type: hub
area: vault
status: current
implementation: not-applicable
tags:
  - igloo
aliases:
  - iGloo knowledge base
reviewed: 2026-10-03
reviewed_commit: 5e4ebe846b488d8609612d37665aaefd4d203c88
sources:
  - README.md
  - ROADMAP.md
  - Igloo.sln
  - src/Igloo.Cli/Igloo.Cli/Program.cs
  - docs/architecture/product-boundaries.md
---

# iGloo Home

> [!IMPORTANT] Current project map
> This page preserves a 3 October 2026 navigation snapshot. Its statements that CLI is only `Hello, World!` and One is an empty folder are superseded by the 10 October working-tree inspection. Open [[00 - Home/Project Dashboard]] for current code, the CLI-mediated target and architecture drift.

iGloo helps people and organizations plan a safer exit from Windows to Linux. This is the central map of the `docs/` vault. It follows the **decided four-product direction** while marking what the inspected Visual Studio solution actually contains. Start with [[current-state|current state]] before relying on a capability claim.

## Four peer products

- [[community|iGloo Community]] — the existing WPF wizard for a person migrating a Windows machine. Its workflow still contains Community-owned orchestration.
- [[cli|iGloo CLI]] — a console project in the local working-tree solution, currently only `Hello, World!`; the proposed commands and machine contract are not implemented.
- [[ai|iGloo AI]] — an empty solution folder today. The intended role is to translate intent into explicit supported choices, never to perform disk operations itself.
- [[fleet-map|iGloo Fleet]] — six Agent, Contracts, Domain, Persistence, Server and Web projects for organization assessment and planning; there is no remote migration execution endpoint.

These products are peers over the [[shared-engine|iGloo Platform]]. The existing `Shared` libraries and `Distros` plugins supply capabilities; `Platform` is a conceptual boundary, not a folder in the current solution. [[architecture/product-boundaries|Product boundaries]] and [[decisions/013-product-family-and-platform|ADR-013]] record the decision. The repository-root `README.md` and `ROADMAP.md` retain older Community-centered presentation and milestones; [[direction|product direction]] records the broader roadmap gap.

## Where to go

- **Begin with reality:** [[current-state|Current project state]] separates working code, partial paths and plans.
- **Understand implementation:** [[architecture/shared-orchestration|Shared orchestration]] identifies work that still lives in Community.
- **Check compatibility and safety:** [[distributions|Distributions]] separates catalog status from install evidence; [[safety-and-evidence|Safety and evidence]] links the guards, data boundaries and hardware records.
- **Plan or contribute:** [[development|Development]] covers tests and release work.
- **Use and maintain this vault:** [[vault/graph-guide|Graph Guide]] explains the focused product graph and full knowledge graph; [[vault/audit|Vault audit]] tracks contradictions and missing knowledge.

## Working constraints

Installation and recovery work can affect partitions, firmware and private user data. A fixture, build or earlier hardware run proves only its recorded scope. Current target ownership, recovery readiness, the exact distro path, and independent evidence must be checked at the point of action. The [safety map](safety-and-evidence.md) distinguishes implemented guards from blocked production claims.

[[current-state]] distinguishes code, plans and retained evidence at the inspected commit. The four-product structure describes ownership and direction; it is not a claim that CLI or AI can install Linux today.
