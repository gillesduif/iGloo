---
title: Source Index
type: reference
product: project
status: mixed
doc_status: current
validation_status: partial
availability: not-applicable
graph_cluster: reference
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources:
  - README.md
  - ROADMAP.md
  - docs/spec/command-contract/README.md
aliases: []
tags:
  - igloo
---

# Source Index

This vault records the working tree on `refactor/community-fleet-foundation` at committed HEAD `366c1ec57c6e5bde6d452208d4ef6411ae99ebf1` as inspected on 10 October 2026. The checkout contains uncommitted and untracked changes; HEAD alone does not identify their bytes. See [[20 - Reference/Source and Whitepaper Version Index]] for source roles and the supplied PDF's version discrepancy.

- **Normative target:** the supplied *iGloo: A CLI-Mediated Architecture for Operating-System Installation and Migration* PDF, 6 October 2026, especially §§3–4, 7–10, 16–20. It describes intended architecture, not present implementation.
- **Current code and public contract:** [Command Contract](../spec/command-contract/README.md), [implementation report](../spec/command-contract/implementation-report.md), `src/Igloo.Core.Runtime/CommandDispatcher.cs`, `src/Igloo.Core.Host/IglooHost.cs`, `src/Igloo.Cli/Igloo.Cli/Program.cs`, `Igloo.sln`, and active project references. The documents are working-tree sources; inspect code when a claim matters.
- **Plans and release context:** [ROADMAP](https://github.com/gillesduif/iGloo/blob/main/ROADMAP.md), [repository README](https://github.com/gillesduif/iGloo/blob/main/README.md), [contribution rules](https://github.com/gillesduif/iGloo/blob/main/CONTRIBUTING.md), and [ADR-013](../decisions/013-product-family-and-platform.md). Their older snapshots can conflict with newer code.
- **Validation records:** [hardware findings](../reference/hardware-findings.md), [Fleet Phase 2](../fleet/phase-2.md), and dated evidence reports. A past test count or hardware run is scoped to its recorded build and environment.
- **Historical publication:** [August migration whitepaper](../whitepaper/igloo-whitepaper.md) remains separate from the supplied CLI-mediated PDF.

Use [[00 - Home/Architecture Drift Register]] when two sources disagree, and [[13 - Testing and Validation/Evidence Register]] before promoting a status claim.
