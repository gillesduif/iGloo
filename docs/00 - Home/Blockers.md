---
title: Blockers
type: navigation
product: project
status: mixed
doc_status: current
validation_status: partial
availability: branch-only
graph_cluster: safety
last_reviewed: 2026-10-25
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [docs/spec/command-contract/README.md, docs/fleet/phase-2.md]
aliases: []
tags: [igloo]
---

# Blockers

The public destructive gateway remains closed because the exact installer-consumed layout, protected secret custody, execution-bound approval, qualified recovery evidence, durable migration state and independent completion verification have not converged. [[10 - Safety and Security/Safety and Security Overview]] explains each gate; [[15 - Implementation/Master Implementation Checklist]] has AC-04–08, the canonical work items. Separately, Community and Fleet Agent retain direct Host dependencies, so whitepaper A2 is not yet fully met: [[00 - Home/Architecture Drift Register]] DR-01/02 and AC-01/03/09/10. These are branch observations, not statements that the 0.2-alpha release changed.

**Not a blocker any more (25 October 2026):** Community boot registration is no longer conditioned on recovery evidence. The CommunityRecovery planner blocked every Debian/Mint/Fedora attempt in the VM lab on a leftover BCD object; registration is the v0.2-alpha NVRAM + bcdedit path again ([ADR-014](../decisions/014-boot-registration-is-not-gated.md)). Recovery qualification remains an open item for Fleet and for the reviewed path, tracked under AC-06, not a precondition for installing. Beta blockers are listed in [[00 - Home/Beta Track]].
