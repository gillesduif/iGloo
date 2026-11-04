---
title: Requirements Traceability Matrix
type: matrix
product: platform
status: mixed
doc_status: current
validation_status: partial
availability: branch-only
graph_cluster: reference
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [docs/spec/command-contract/README.md, src/Igloo.Core.Runtime/CommandDispatcher.cs, src/Igloo.Core.Host/IglooHost.cs]
aliases: []
tags: [igloo]
---

# Requirements Traceability Matrix

The supplied whitepaper's A1–A10 Table 1 is on pages 7–8; more detail is in §§4–10, 13–16 and 19. [[02 - Architecture/Invariant Conformance Matrix]] has each invariant's meaning/current gap/test. This compact map connects target to public surface, code, workflow, safety and canonical task. The PDF source version remains unresolved in [[20 - Reference/Source and Whitepaper Version Index]].

| PDF requirement | Public surface / current path | Workflow and safety gate | Present test source or missing proof | Task |
| --- | --- | --- | --- | --- |
| A1–A3, §§3–5: one Core/CLI semantic path | CLI → Host/Runtime; Community and Agent still Host; One → Cli.Client | [[09 - Workflows/Workflow Overview]] current vs target | `CommandBoundaryTests`, `CliProcessBoundaryTests`; full parity missing | AC-01/03/09/10 |
| A4/A10, §§5/10: protocol purity and secrets | `invoke --machine`, Command.Contracts schemas | stdin request, structured result; protected secret handle absent | process tests; leak and packaging tests missing | AC-02/05 |
| A5, §§8–9: durable operation | `operation.status`, ISO journal; no migration producer | cross-reboot state/leases missing | ISO tests; migration replay/hardware missing | AC-07/08 |
| A6, §12: Server isolation | Fleet Server Domain/Persistence; Agent Cli.Client and Host | sanitized assessment/dry run; no remote mutation | Fleet architecture and assessment tests; destructive path not claimed | AC-10 |
| A7, §4: provider independence | Core abstraction/provider projects | distro-specific installer and first boot | provider fixtures and historical hardware; current parity incomplete | AC-11 |
| A8/A9, §§7/15: authority and verified completion | advisory plan/target/allocation; prepare/execute/verify absent | [[10 - Safety and Security/Plan Target Allocation and Artifact Bindings]] | stale-state fixtures; exact recovery, approval, postconditions missing | AC-04–08 |

The canonical implementation sources are linked from [[00 - Home/Source Index]]; this matrix is a navigation aid, not a second schema or test result.
