---
title: Safety and Security Overview
type: moc
product: core
status: mixed
doc_status: current
validation_status: partial
availability: branch-only
graph_cluster: safety
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [docs/reference/safety-model.md, docs/fleet/phase-2.md, docs/spec/command-contract/README.md]
aliases: []
tags: [igloo]
---

# Safety and Security Overview

The whitepaper A8–A10 and §§7–10, 13–15 set the target: a request never grants its own authority; completion requires observed postconditions; secrets never travel in argv. [[10 - Safety and Security/Plan Target Allocation and Artifact Bindings]] separates advisory identities from execution authority. [[02 - Architecture/Invariant Conformance Matrix]] maps all ten invariants to current code and missing tests.

**CODE OBSERVATION:** `recovery.inspect` does not establish exact production readiness. The [Fleet Phase 2 record](../fleet/phase-2.md) describes unresolved WinRE/BCD/EFI qualification; [safety model](../reference/safety-model.md) describes older runtime safeguards. Public destructive migration/install/recovery commands are absent. Private Community workflows do not make the public command contract safe for those operations. Current ISO acquisition verifies cache artifacts and has a limited journal; it does not prove whole-system restoration.

The main closed gates are complete layout and artifact binding; protected cross-process secret custody, especially the private Wi-Fi/manifest path; exact authority bound to state and validity interval; risk-proportional recovery proof; resource locks owned by a durable operation; replay-safe Windows/installer/first-boot continuation; and independent postcondition verification. [[02 - Architecture/Architecture Convergence Plan]] orders these tasks. [[13 - Testing and Validation/Test Matrix]] requires negative and failure-injection evidence before any status promotion.

**Safe operating rule:** When a live mutation is not qualified, keep the command unregistered and record an `UNKNOWN` or unavailable observation. Do not manufacture an apparent RecoveryId, secret handle, approval or successful migration result from a plan or a green unit suite. See [existing safety and evidence map](../safety-and-evidence.md) and [[17 - Risks and Issues/Risk Register]].
