---
title: Master Implementation Checklist
type: checklist
product: platform
status: proposed
doc_status: current
validation_status: unverified
availability: not-applicable
graph_cluster: delivery
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [docs/spec/command-contract/implementation-report.md, docs/fleet/phase-2.md, ROADMAP.md]
aliases: []
tags: [igloo]
---

# Master Implementation Checklist

These AC items are **PROPOSALS derived from whitepaper §§18–20 and observed gaps**. They are the only checkboxes for this vault's architecture convergence work. No owner or date is assigned without a planning source. [[02 - Architecture/Architecture Convergence Plan]] explains order; [[13 - Testing and Validation/Test Matrix]] defines acceptance layers. A box stays open until source change and the named test evidence are both recorded.

| ID | Source and dependency | Blocker / current evidence | Acceptance evidence |
| --- | --- | --- | --- |
| AC-01 | PDF A2; DR-01/02 | Shared Host allowance conflicts with process target | Reviewed ADR plus aligned spec and transition scope |
| AC-02 | PDF A3/A4/A10; AC-01 | CLI Client exists, packaging/signature assurance open | Real process compatibility, substitution and failure tests |
| AC-03 | PDF A2/A3; AC-02 | Community/Agent Host edges | Cross-client parity plus prohibitive architecture tests |
| AC-04 | PDF §7.4; AC-03 | Advisory allocation covers only NTFS shrink | Exact complete writer/readback and installer geometry tests |
| AC-05 | PDF A8/A10; AC-04 | No protected SecretHandle/approval binding | Lifecycle, freshness, leak and authorization tests |
| AC-06 | PDF A8; AC-04 | Exact recovery remains unavailable | Independently verified risk-proportional restoration evidence |
| AC-07 | PDF A5/A8; AC-04–06 | `migration.prepare` unregistered | Atomic revalidation, leases, durable OperationId |
| AC-08 | PDF A5/A9; AC-07 | No cross-reboot migration producer/verifier | Crash/replay/reboot/postcondition and failure-state tests |
| AC-09 | PDF A1/A2; AC-03 and AC-08 | Community private Host mutation path | CLI semantic parity and safe transition VM evidence |
| AC-10 | PDF A2/A6; AC-03 | Fleet Agent Host adapter | Agent-only CLI tests; Server boundary and sanitized protocol proof |
| AC-11 | PDF A7; AC-08 | Provider evidence varies by distro | Fixture, installer, first-boot and hardware matrix |
| AC-12 | PDF §§16/19; AC-02 and AC-11 | Release and branch claims differ | Published package, version, trust, hardware and support review |

- [ ] **AC-01** Record the accepted executable-boundary decision and transitional Host exception; resolve DR-01/02.
- [ ] **AC-02** Qualify CLI placement, product/protocol negotiation, machine framing, process loss and executable provenance.
- [ ] **AC-03** Prove read-only/planning parity across CLI human, machine, Community and Fleet before blocking Host bypasses.
- [ ] **AC-04** Bind exact complete installer layout and artifacts, with independent writer/readback and provider consumption.
- [ ] **AC-05** Implement protected secret custody and execution approval bound to canonical intent, identity and revisions.
- [ ] **AC-06** Qualify exact recovery readiness at the risk required for destructive transitions.
- [ ] **AC-07** Add executable prepare only after AC-04–06, with durable OperationId and exclusive resource ownership.
- [ ] **AC-08** Prove replay-safe cross-reboot continuation and postcondition-based execute/verify semantics before registration.
- [ ] **AC-09** Migrate Community private destructive paths to CLI after safety/semantic parity, then remove direct Host bypass.
- [ ] **AC-10** Remove transitional Fleet Agent Host paths while keeping Server isolated and remote destructive work disabled until separately authorized and proven.
- [ ] **AC-11** Enforce provider independence and qualify each distro's installer/first-boot path with scoped evidence.
- [ ] **AC-12** Reconcile branch, shipped release, packaging, signing/trust, hardware scope and support claims.

External [GitHub milestones](https://github.com/gillesduif/iGloo/milestones) remain the official release planning index. None of the AC items is silently assigned to a milestone.
