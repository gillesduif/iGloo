---
title: Risk Register
type: risk-register
product: platform
status: mixed
doc_status: current
validation_status: partial
availability: branch-only
graph_cluster: safety
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [docs/business/risk-register.md, docs/spec/command-contract/implementation-report.md, docs/fleet/phase-2.md]
aliases: []
tags: [igloo]
---

# Risk Register

This is an architecture risk index, not a replacement for the historical [business risk register](../business/risk-register.md). Likelihood and owner are unassigned because no current assessment/assignment was reviewed. [[15 - Implementation/Master Implementation Checklist]] owns the corresponding tasks.

| ID | Observed issue or potential risk | Consequence | Current control and remaining gap | Task |
| --- | --- | --- | --- | --- |
| R-01 | **Observed:** Community/Fleet Agent retain Host edges against target A2 | Divergent semantics or bypass | Private paths remain explicit; require parity and architecture guard | AC-01/03/09/10 |
| R-02 | **Observed:** advisory AllocationId does not bind complete installer geometry | Wrong partition/ESP handling | Public prepare/execute absent; exact writer/readback and provider parity needed | AC-04 |
| R-03 | **Observed:** private sensitive manifest/Wi-Fi lifetime is not a safe public transport | Credential exposure | Keep manifest private; design protected custody and leak tests | AC-05 |
| R-04 | **Observed:** exact recovery readiness unavailable | Irrecoverable boot/storage state | Destructive public gate closed; independent recovery qualification needed | AC-06 |
| R-05 | **Observed:** ISO journal does not span migration reboot | Orphaned operation or uncertain retry | No public migration producer; design durable state and locks | AC-07/08 |
| R-06 | **Potential:** stale or substituted CLI binary accepted by location/metadata checks | Wrong protocol or malicious gateway | Hello/metadata checks exist, not cryptographic provenance | AC-02/12 |
| R-07 | **Observed:** old release/hardware documents may be read as current branch proof | Overstated support | Dashboard separates release from branch; require exact evidence review | AC-12 |
| R-08 | **Potential:** Fleet data/approval conflated with endpoint authority | Remote destructive action without Core admission | Server isolated and no destructive work type; keep Agent/Core gates | AC-05/10 |

Do not close a risk because a note or fixture exists. The [hardware findings](../reference/hardware-findings.md) and [Fleet recovery blocker](../fleet/phase-2.md) provide more specific evidence. [[13 - Testing and Validation/Test Matrix]] names acceptance checks.
