---
title: Invariant Conformance Matrix
type: matrix
product: platform
status: mixed
doc_status: current
validation_status: partial
availability: branch-only
graph_cluster: architecture
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [Igloo.sln, docs/spec/command-contract/README.md, tests/Igloo.Command.Tests/CommandBoundaryTests.cs, tests/Igloo.Command.Tests/CliProcessBoundaryTests.cs, tests/Igloo.Fleet.Tests/ArchitectureTests.cs]
aliases: []
tags: [igloo]
---

# Invariant Conformance Matrix

The source for all target statements is the supplied whitepaper's Table 1, pages 7–8 (§3), elaborated in §§4–10, 13–16 and 19. Current observations refer to the 10 October dirty working tree at `366c1ec`. Existing tests named here were inspected, not automatically rerun by this document.

| Invariant | Meaning and scope | Current conformance and enforcement | Uncovered path / gap | Proof needed and canonical task |
| --- | --- | --- | --- | --- |
| A1 Single execution authority | Core owns endpoint inspection and mutation semantics, including disk, boot, migration, installer and recovery (§§3–4) | Host composes Core services and shared dispatcher; providers remain below Core | Community private orchestration still coordinates high-impact workflow | Static dependency and behavior parity tests, then safe VM/hardware checks; AC-03, AC-09 |
| A2 Single executable gateway | All first-party endpoint clients cross CLI **process** (§3) | One shell and Fleet development assessment use Cli.Client; CLI hosts dispatcher | Community and Fleet Agent retain direct Host references; tests allow them | Failing architecture rule for prohibited direct references plus end-to-end CLI parity; AC-01, AC-03, AC-10 |
| A3 One semantic path | Human and machine modes resolve to identical typed capability semantics (§§3, 5) | Both CLI paths use dispatcher; `CliProcessBoundaryTests` exercises a narrow parity case | Full capability/version/client matrix missing | Golden semantics across human/JSON/machine and clients; AC-03 |
| A4 Machine stdout purity | Machine stdout contains only protocol frames; stderr is diagnostic (§5) | Machine framing and JSON tests exist; result is semantic authority | Packaging, failures after accepted work and malformed stream coverage need expansion | Black-box capture under failures and cancellation; AC-02 |
| A5 Core owns durability | State, locks and recovery survive client/CLI exit and reboot (§§8–9) | ISO file journal and selected cross-process leases exist | No migration OperationId producer or Windows-to-first-boot journal | Replay, crash, reboot and reconnection tests with independent readback; AC-07, AC-08 |
| A6 Fleet Server isolation | Server is control plane; only Agent can address endpoint CLI (§§3–4, 12) | Server references Fleet Domain/Persistence only; Fleet work remains assessment/dry run | No evidence for destructive endpoint translation, which remains out of scope | Architecture test plus sanitized network protocol tests; AC-10 |
| A7 Provider independence | Distro/OEM providers supply deltas below Core (§§3–4) | Provider projects reference Core abstraction; distro manifests/scripts exist | Installer rendering and first-boot proof differ by distro | Project-reference guard plus provider fixture, VM and physical evidence; AC-11 |
| A8 No authority by intent | UI, One, signed job, PlanId or `--yes` cannot authorize mutation (§§7, 13) | Advisory PlanId/TargetId/AllocationId are non-executable; recovery reports unavailable | No execution approval artifact joined to exact state, secrets and recovery | Stale-state, identity, approval, secret and privilege negative tests; AC-04–AC-06 |
| A9 Observable completion | Exit 0 never substitutes for verified postconditions (§§8, 15) | ISO acquisition verifies artifacts; no public migration verifier | Installer/first-boot postconditions and evidence absent | Fault injection and independent postcondition readback; AC-08 |
| A10 No secrets in argv | Secret transport uses protected transient input or opaque local handle (§10) | Machine requests use stdin; no public sensitive migration command | Private Community manifest/Wi-Fi path still needs safe custody design | Secret-leak scans across argv/stdout/stderr/journal/Fleet and lifecycle tests; AC-05 |

**Status:** Partial target conformance. No single test run or green architecture suite proves all ten. [[13 - Testing and Validation/Test Matrix]] separates present tests from missing acceptance evidence; [[00 - Home/Architecture Drift Register]] records unresolved source conflicts.
