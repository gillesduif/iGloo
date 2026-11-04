---
title: Architecture Convergence Plan
type: workstream
product: platform
status: proposed
doc_status: current
validation_status: unverified
availability: not-applicable
graph_cluster: architecture
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [docs/spec/command-contract/README.md, docs/spec/command-contract/implementation-report.md, ROADMAP.md]
aliases: []
tags: [igloo]
---

# Architecture Convergence Plan

**PROPOSAL:** Safety-ordered interpretation of the supplied whitepaper §§18–20, refined against the current working tree. These are not accepted release milestones or permission to open destructive capabilities. [[15 - Implementation/Master Implementation Checklist]] owns the single canonical checkbox for each AC item; this page explains dependencies and acceptance evidence. GitHub [milestones](https://github.com/gillesduif/iGloo/milestones) remain the release-planning source referenced by [ROADMAP](https://github.com/gillesduif/iGloo/blob/main/ROADMAP.md).

| Order | Work | Dependency and minimum proof |
| --- | --- | --- |
| AC-01 | Decide CLI executable-gateway governance and transitional Host exception | Resolve DR-01/02 in a reviewed ADR; align Command Contract without erasing current code facts. |
| AC-02 | Finish trusted CLI location, product/protocol negotiation, schema/version rules and stdout purity | Existing Cli.Client and framing tests are a start; prove packaging, substitution response and transport failure semantics. |
| AC-03 | Migrate read-only Community and Fleet use through CLI after semantic parity | Verify same typed results and error codes through real processes, then add architecture tests that fail on unauthorized direct Host edges. |
| AC-04 | Bind PlanId, TargetId, ArtifactId and a complete allocation/layout as distinct non-authorizing facts | Current advisory `AllocationId` covers NTFS shrink intent only. Require deterministic artifact and provider revisions, exact readback and installer-consumed geometry. |
| AC-05 | Design protected secret custody and exact approval artifact | Verify creation, authorized consumer, expiration, one-shot use, cleanup and no leaks to argv, public results, journals or Fleet. Bind approval to canonical intent and revisions. |
| AC-06 | Prove risk-proportional recovery readiness | Exact WinRE/BCD/EFI and restoration evidence must be independent and qualified. Keep `ObservationUnavailable` when proof is missing. |
| AC-07 | Register executable preparation only when AC-04–06 pass together | Core revalidates at commit, acquires leases and creates a durable migration OperationId before irreversible work. No public `migration.prepare` today. |
| AC-08 | Add replay-safe installer/first-boot continuation and postcondition verification | Crash/reboot/failure injection, status reconnection, explicit failed versus recovery-required states; only then consider execute/verify registrations. |
| AC-09 | Move Community's private destructive Host path through CLI | Require identical semantics and safe transition tests for staging, install, cleanup, USB and recovery before removing direct Host access. |
| AC-10 | Remove transitional Fleet Agent Host path without changing Server isolation | Agent local CLI translation must retain trusted authorization and sanitization; no remote destructive job is implied. |
| AC-11 | Enforce provider/client separation and public conformance | Provider reference guard, distro fixtures, installer/first-boot VM and hardware evidence, external protocol promise only after tested first-party parity. |
| AC-12 | Reconcile release/package/hardware claims | Verify shipped bits, signed/trusted CLI placement, distro-specific current evidence and support path; update release docs separately from branch-only architecture. |

The sequence is semantic: a passing build, schema or advisory binding alone does not advance AC-07 or AC-08. [[13 - Testing and Validation/Test Matrix]] defines the proof layers; [[17 - Risks and Issues/Risk Register]] records hazards. The older [Community roadmap](https://github.com/gillesduif/iGloo/blob/main/ROADMAP.md) and [Fleet Phase 2 status](../fleet/phase-2.md) retain their separate scopes.
