---
title: Architecture Gap Analysis
type: analysis
product: platform
status: mixed
doc_status: current
validation_status: partial
availability: branch-only
graph_cluster: architecture
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [Igloo.sln, docs/spec/command-contract/README.md, src/Igloo.Core.Host/IglooHost.cs, src/Igloo.Core.Runtime/CommandDispatcher.cs]
aliases: []
tags: [igloo]
---

# Architecture Gap Analysis

**CODE OBSERVATION versus NORMATIVE TARGET:** This is the transition view between [[02 - Architecture/Current Architecture]] and [[02 - Architecture/Target Architecture]]. The target is the supplied 6 October whitepaper (cover v1.0, interior v1.1), especially A1–A10 on pages 7–8; the present code is the dirty 10 October working tree at committed HEAD `366c1ec`. [[02 - Architecture/Invariant Conformance Matrix]] covers each invariant; [[00 - Home/Architecture Drift Register]] records conflicting sources; [[02 - Architecture/Architecture Convergence Plan]] orders work. The IDs below are **PROPOSAL** groupings, not public commands or accepted release milestones.

| Gap / invariant / source | Current path and public capability | Target interaction and missing identity, contract or authority | Risk and current evidence | Dependency, canonical task, status and acceptance test |
| --- | --- | --- | --- | --- |
| G-01 · A2 · PDF §3 | Community and Fleet Agent `.csproj` files reference Host; One and some Agent assessment paths use Cli.Client; CLI hosts dispatcher | Every first-party endpoint client crosses the CLI process. Adopt an A2 boundary decision and a compatible, trusted CLI package. | Direct Host paths can bypass the intended executable gateway; project references and contract prose inspected, not runtime parity proof. | Decide scope before removal; AC-01/02/03/10; **open**. An architecture test fails on prohibited client→Host references and black-box CLI tests pass for equivalent operations. |
| G-02 · A3/A4 · PDF §§5–6 | `invoke --machine` and typed V1 `capabilityId`/`capabilityVersion` exist; public registry has 19 IDs/20 ID-version descriptors | Human, JSON and machine routes preserve one semantic result/error/event contract across supported clients, versions, cancellation and malformed transport. | Selected command tests exist; 44/44 focused Command tests passed on this dirty tree, but no complete cross-client matrix was run. | CLI packaging and a stable registry first; AC-02/03; **partial**. Golden protocol and process-exit tests cover every registered capability and failure mode. |
| G-03 · A8 · PDF §§7/13 | `migration.plan`, `migration.target.bind` and narrow NTFS-shrink `migration.target.allocate` are advisory; no public `prepare` | Separate PlanId, TargetId, AllocationId or full layout identity, ArtifactId, RecoveryId, SecretHandle and exact Approval binding. An advisory ID grants no mutation authority. | Incomplete installer-consumed geometry could make an apparently matched plan unsafe; source/schema inspected, not actual-disk readback. | Complete layout and independent readback before authority; AC-04/05/06; **blocked**. Stale-state and mismatched-identity negatives fail closed before any disk/boot action. |
| G-04 · A5/A9 · PDF §§8–9/15 | ISO acquisition has a scoped journal and locks; `operation.status` exists without a public migration OperationId producer | Core owns durable cross-reboot migration state, leases, replay, first-boot continuation and observed postconditions. | A successful CLI exit or ISO receipt cannot prove installation or recovery; no migration execute/verify handler or reboot-qualified evidence. | G-03 and exact recovery first; AC-07/08; **blocked**. Crash/reboot/replay tests resume by OperationId and independently verify intended target state. |
| G-05 · A9/A10 · PDF §§10/15 | Machine request uses stdin; private Community manifest and Wi-Fi paths remain; `recovery.inspect` cannot claim exact readiness | Protected ephemeral secret custody, exact recovery qualification and postcondition verifier bind to the same reviewed operation. | Leakage and non-restorable rollback remain unresolved; code/test definitions inspected, no production recovery proof. | AC-05/06/08; **blocked**. Leak scans, custody lifecycle, recovery readback and injected-failure restoration pass in qualified environments. |
| G-06 · A6/A7 · PDF §§4/12 | Fleet Server references Domain/Persistence; Agent has Client plus Host paths; provider projects sit below Core abstractions | Server remains control plane; Agent alone translates authorized endpoint work through CLI; providers supply data/configuration deltas under Core. | Project boundaries support the intended shape but do not prove remote destructive authority or distro installer conformance. | G-01 before Agent Host removal, provider fixtures before hardware claims; AC-10/11; **partial**. Reference guards, sanitized network tests and distro VM/physical acceptance evidence pass. |

These gaps are linked to one checkbox each in [[15 - Implementation/Master Implementation Checklist]]; do not create a second checklist here. [[13 - Testing and Validation/Test Matrix]] defines evidence layers, and [[10 - Safety and Security/Safety and Security Overview]] keeps destructive public capability registration closed until the exact gates are met.
